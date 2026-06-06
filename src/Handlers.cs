using System.Buffers;
using System.IO.Pipelines;

/// <summary>
/// Handler do POST /fraud-score. Caminho quente: leitura do corpo -> vetor (na
/// stack) -> kNN -> escrita de uma resposta pré-computada. Nunca devolve 5xx ao
/// cliente: qualquer falha vira fallback approved=true/score=0 (HTTP 200).
/// </summary>
internal static class Handlers
{
    public static async Task Fraud(HttpContext ctx, FraudStore store)
    {
        var res = ctx.Response;

        // Se a instância ainda não carregou o dataset, devolve 503 para o nginx
        // reencaminhar (proxy_next_upstream) à instância que já está pronta.
        // Isso só acontece na janela de startup; durante o teste medido as duas
        // instâncias já estão prontas e nenhum 503 é emitido.
        if (!store.Ready)
        {
            res.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        int fraud = 0;
        var reader = ctx.Request.BodyReader;
        try
        {
            // Lê o corpo inteiro (Content-Length pequeno -> 1 leitura).
            ReadResult rr;
            while (true)
            {
                rr = await reader.ReadAsync();
                if (rr.IsCompleted || rr.IsCanceled) break;
                reader.AdvanceTo(rr.Buffer.Start, rr.Buffer.End);
            }

            var buffer = rr.Buffer;
            try { fraud = Compute(buffer, store); }
            finally { reader.AdvanceTo(buffer.End); }
        }
        catch
        {
            fraud = 0; // fallback rápido (approved=true, score=0.0)
        }

        if ((uint)fraud > 5u) fraud = 0; // blindagem do índice

        var bytes = store.Responses[fraud];
        res.StatusCode = StatusCodes.Status200OK;
        res.ContentType = "application/json";
        res.ContentLength = bytes.Length;
        await res.Body.WriteAsync(bytes);
    }

    // Síncrono de propósito: o Span<double> (ref struct) não pode cruzar um await.
    private static int Compute(in ReadOnlySequence<byte> buffer, FraudStore store)
    {
        Span<double> v = stackalloc double[14];
        byte[]? rented = null;
        try
        {
            ReadOnlySpan<byte> span;
            if (buffer.IsSingleSegment)
            {
                span = buffer.FirstSpan;          // caminho comum: zero cópia
            }
            else
            {
                int len = (int)buffer.Length;
                rented = ArrayPool<byte>.Shared.Rent(len);
                buffer.CopyTo(rented);
                span = rented.AsSpan(0, len);
            }

            Vectorizer.BuildVector(span, v);
            return store.CountTop5(v);
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
