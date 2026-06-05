#!/usr/bin/env python3
import sys, gzip, json, struct, array
import numpy as np
from sklearn.cluster import MiniBatchKMeans

SCALE = 8000
SENT = -SCALE
DIMS = 14
PAD = 16
MAGIC = 0x52494E48
NUM_CLUSTERS = 1024

def q(x: float) -> int:
    if x < 0:
        return SENT
    if x > 1.0:
        x = 1.0
    return int(x * SCALE + 0.5)

def main(src: str, dst: str) -> None:
    with gzip.open(src, "rt") as f:
        s = f.read()

    dec = json.JSONDecoder()
    vecs = array.array("h")    
    labs = array.array("B")    
    pos = s.find("[") + 1
    n = 0
    while True:
        while pos < len(s) and s[pos] in " \n\r\t,":
            pos += 1
        if pos >= len(s) or s[pos] == "]":
            break
        obj, end = dec.raw_decode(s, pos)
        v = obj["vector"]
        for d in range(DIMS):
            vecs.append(q(v[d]))
        vecs.append(0)         
        vecs.append(0)         
        labs.append(1 if obj["label"] == "fraud" else 0)
        n += 1
        pos = end

    print(f"Loaded {n} vectors. Running KMeans to create {NUM_CLUSTERS} clusters...")
    
    # Transforma em numpy arrays
    X = np.frombuffer(vecs, dtype=np.int16).reshape(-1, PAD)
    Y = np.frombuffer(labs, dtype=np.uint8)

    # Treina os clusters
    kmeans = MiniBatchKMeans(n_clusters=NUM_CLUSTERS, batch_size=10240, n_init='auto', random_state=42)
    cluster_ids = kmeans.fit_predict(X)
    centroids = np.round(kmeans.cluster_centers_).astype(np.int16)

    # Ordena os vetores e labels pelos clusters
    sorted_indices = np.argsort(cluster_ids)
    X_sorted = X[sorted_indices]
    Y_sorted = Y[sorted_indices]
    cluster_ids_sorted = cluster_ids[sorted_indices]

    # Calcula onde cada cluster começa e quantos vetores tem
    cluster_counts = np.bincount(cluster_ids_sorted, minlength=NUM_CLUSTERS).astype(np.int32)
    cluster_offsets = np.concatenate(([0], np.cumsum(cluster_counts)[:-1])).astype(np.int32)

    with open(dst, "wb") as o:
        # Header: Magic(4), Count(4), Dims(4), Scale(4), NumClusters(4)
        o.write(struct.pack("<iiiii", MAGIC, n, PAD, SCALE, NUM_CLUSTERS))
        centroids.tofile(o)
        cluster_offsets.tofile(o)
        cluster_counts.tofile(o)
        X_sorted.tofile(o)
        Y_sorted.tofile(o)
        
    print(f"Wrote IVF index: {dst}")

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])