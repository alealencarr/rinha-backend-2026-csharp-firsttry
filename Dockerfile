# =============================================================================
# Build NativeAOT (linux-x64 == linux-amd64). Construa SEMPRE com:
#   docker buildx build --platform linux/amd64 ...
# A Rinha roda num Mac Mini Intel (amd64).
# =============================================================================
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Toolchain do NativeAOT no Linux
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Cache de restore
COPY src/Rinha.Fraud.csproj ./src/
RUN dotnet restore ./src/Rinha.Fraud.csproj -r linux-x64

# Código + recursos
COPY src/ ./src/
COPY resources/ ./resources/

# Publica o binário nativo
RUN dotnet publish ./src/Rinha.Fraud.csproj -c Release -r linux-x64 \
    --no-restore -o /publish /p:PublishAot=true

# Descomprime o dataset no build => sem dependência de zlib em runtime
# e startup mais rápido (lê JSON puro).
RUN gzip -dc resources/references.json.gz > /publish/references.json \
 && ls -lh /publish/references.json

# =============================================================================
# Runtime mínimo (chiseled, sem shell, non-root). Só precisa de glibc/libstdc++.
# =============================================================================
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-noble-chiseled AS final
WORKDIR /app

COPY --from=build /publish/rinha            /app/rinha
COPY --from=build /publish/references.json  /app/references.json

ENV ASPNETCORE_URLS=http://+:8080 \
    RESOURCES_DIR=/app \
    DOTNET_gcServer=0 \
    DOTNET_GCHeapHardLimitPercent=75 \
    DOTNET_TieredPGO=0

EXPOSE 8080
ENTRYPOINT ["/app/rinha"]
