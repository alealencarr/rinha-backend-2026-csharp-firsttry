using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FraudApi;

public sealed unsafe class KnnIndex : IDisposable
{
    private const int Pad = Vectorizer.Pad;
    private const int NProbe = 8; // O limite da velocidade mantendo a acurácia.

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;

    private readonly short* _centroids;
    private readonly int* _clusterOffsets;
    private readonly int* _clusterCounts;
    private readonly short* _vectors;
    private readonly byte* _labels;

    private readonly int[] _norms;

    public int Count { get; }
    public int NumClusters { get; }

    public KnnIndex(string path)
    {
        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        byte* ptr = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _base = ptr;

        int magic = *(int*)(_base + 0);
        int count = *(int*)(_base + 4);
        int dims = *(int*)(_base + 8);
        int numClusters = *(int*)(_base + 16);

        if (magic != 0x52494E48) throw new InvalidDataException("bad magic");

        Count = count;
        NumClusters = numClusters;

        long offset = 20;

        _centroids = (short*)(_base + offset);
        offset += numClusters * Pad * sizeof(short);

        _clusterOffsets = (int*)(_base + offset);
        offset += numClusters * sizeof(int);

        _clusterCounts = (int*)(_base + offset);
        offset += numClusters * sizeof(int);

        _vectors = (short*)(_base + offset);
        offset += (long)count * Pad * sizeof(short);

        _labels = (byte*)(_base + offset);

        _norms = new int[count];
        short* vPtr = _vectors;
        for (int i = 0; i < count; i++, vPtr += Pad)
        {
            int norm = 0;
            for (int j = 0; j < Pad; j++) norm += vPtr[j] * vPtr[j];
            _norms[i] = norm;
        }
    }

    public double Score(ReadOnlySpan<short> query)
    {
        int qNorm = 0;
        Vector256<short> q = default;

        // Magia 1: Calcula a norma da query já usando SIMD e Soma Horizontal
        if (Avx2.IsSupported)
        {
            q = Vector256.Create(query[0], query[1], query[2], query[3], query[4], query[5], query[6], query[7],
                                 query[8], query[9], query[10], query[11], query[12], query[13], query[14], query[15]);
            Vector256<int> sq = Avx2.MultiplyAddAdjacent(q, q);
            Vector128<int> s = Sse2.Add(sq.GetLower(), sq.GetUpper());
            s = Sse2.Add(s, Sse2.Shuffle(s, 0x4E));
            s = Sse2.Add(s, Sse2.Shuffle(s, 0xB1));
            qNorm = s.ToScalar();
        }
        else
        {
            for (int i = 0; i < Pad; i++) qNorm += query[i] * query[i];
        }

        Span<long> bestClusterDist = stackalloc long[NProbe];
        Span<int> bestClusterId = stackalloc int[NProbe];
        bestClusterDist.Fill(long.MaxValue);
        bestClusterId.Fill(-1);
        long worstClusterDist = long.MaxValue;

        short* cPtr = _centroids;
        for (int i = 0; i < NumClusters; i++, cPtr += Pad)
        {
            long d = Avx2.IsSupported ? ComputeDistAvx2Euclidean(cPtr, q) : ComputeDistScalarEuclidean(cPtr, query);
            if (d < worstClusterDist) worstClusterDist = InsertCluster(bestClusterDist, bestClusterId, d, i);
        }

        Span<long> bestDist = stackalloc long[5];
        Span<byte> bestLabel = stackalloc byte[5];
        bestDist.Fill(long.MaxValue);
        long worstDist = long.MaxValue;

        for (int p = 0; p < NProbe; p++)
        {
            int cid = bestClusterId[p];
            if (cid == -1) continue;

            int cOffset = _clusterOffsets[cid];
            int cCount = _clusterCounts[cid];

            short* vPtr = _vectors + (cOffset * Pad);
            byte* lPtr = _labels + cOffset;
            int vGlobalIdx = cOffset;

            for (int i = 0; i < cCount; i++, vPtr += Pad, lPtr++, vGlobalIdx++)
            {
                long dotProduct = Avx2.IsSupported ? ComputeDotAvx2(vPtr, q) : ComputeDotScalar(vPtr, query);
                long d = _norms[vGlobalIdx] + qNorm - (dotProduct << 1);

                if (d < worstDist) worstDist = Insert(bestDist, bestLabel, d, *lPtr);
            }
        }

        int frauds = bestLabel[0] + bestLabel[1] + bestLabel[2] + bestLabel[3] + bestLabel[4];
        return frauds / 5.0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeDotAvx2(short* v, Vector256<short> q)
    {
        Vector256<short> r = Avx.LoadVector256(v);
        Vector256<int> sq = Avx2.MultiplyAddAdjacent(r, q);
        Vector128<int> s = Sse2.Add(sq.GetLower(), sq.GetUpper());
        // Magia 2: Soma Horizontal sem sair do SIMD (Shuffle bit a bit)
        s = Sse2.Add(s, Sse2.Shuffle(s, 0x4E)); // Swap high/low 64 bits
        s = Sse2.Add(s, Sse2.Shuffle(s, 0xB1)); // Swap 32 bits
        return s.ToScalar();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeDotScalar(short* v, ReadOnlySpan<short> q)
    {
        long dot = 0;
        for (int k = 0; k < Pad; k++) dot += (long)v[k] * q[k];
        return dot;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeDistAvx2Euclidean(short* v, Vector256<short> q)
    {
        Vector256<short> r = Avx.LoadVector256(v);
        Vector256<short> diff = Avx2.Subtract(r, q);
        Vector256<int> sq = Avx2.MultiplyAddAdjacent(diff, diff);
        Vector128<int> s = Sse2.Add(sq.GetLower(), sq.GetUpper());
        s = Sse2.Add(s, Sse2.Shuffle(s, 0x4E));
        s = Sse2.Add(s, Sse2.Shuffle(s, 0xB1));
        return s.ToScalar();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeDistScalarEuclidean(short* v, ReadOnlySpan<short> q)
    {
        long d = 0;
        for (int k = 0; k < Pad; k++) { int diff = v[k] - q[k]; d += (long)diff * diff; }
        return d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long InsertCluster(Span<long> dist, Span<int> ids, long d, int id)
    {
        int pos = NProbe - 1;
        while (pos > 0 && dist[pos - 1] > d) { dist[pos] = dist[pos - 1]; ids[pos] = ids[pos - 1]; pos--; }
        dist[pos] = d; ids[pos] = id;
        return dist[NProbe - 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Insert(Span<long> dist, Span<byte> label, long d, byte lab)
    {
        int pos = 4;
        while (pos > 0 && dist[pos - 1] > d) { dist[pos] = dist[pos - 1]; label[pos] = label[pos - 1]; pos--; }
        dist[pos] = d; label[pos] = lab;
        return dist[4];
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}