# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Stage 1 — preprocess the dataset (references.json.gz -> int16 IVF binary).
# ---------------------------------------------------------------------------
FROM --platform=linux/amd64 python:3.12-slim AS data
WORKDIR /w
# Instalamos as bibliotecas matemáticas para rodar o K-Means rápido
RUN pip install --no-cache-dir scikit-learn numpy
COPY preprocess.py .
COPY resources/references.json.gz .
RUN python preprocess.py references.json.gz references.i16.bin

# ---------------------------------------------------------------------------
# Stage 2 — Native AOT build.
# ---------------------------------------------------------------------------
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:9.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY src/Api.csproj ./
RUN dotnet restore -r linux-x64
COPY src/ ./
RUN dotnet publish Api.csproj -c Release -r linux-x64 --no-restore -o /app

# ---------------------------------------------------------------------------
# Stage 3 — runtime.
# ---------------------------------------------------------------------------
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/runtime-deps:9.0 AS final
WORKDIR /app
COPY --from=build /app/Api ./Api
COPY --from=data  /w/references.i16.bin /data/references.i16.bin

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV INDEX_PATH=/data/references.i16.bin
ENV DOTNET_gcServer=0
ENV DOTNET_GCHeapHardLimit=0

EXPOSE 8080
ENTRYPOINT ["/app/Api"]