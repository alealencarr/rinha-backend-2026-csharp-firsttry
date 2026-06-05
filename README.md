# Rinha de Backend 2026 — Detecção de fraude (.NET 9 / AOT)

Submissão para a [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026) (detecção de fraude por busca vetorial).

## Stack Técnica

- **.NET 9 + Native AOT**: Eliminação de JIT e *warmup*, garantindo latência estável (*p99* baixo) desde a primeira requisição.
- **Memory-Mapped I/O**: O dataset de ~96MB é mapeado diretamente em memória (`mmap`), permitindo que o SO gerencie o *page cache* de forma eficiente sem inflar o *heap* do Garbage Collector.
- **SIMD AVX2**: A busca vetorial utiliza instruções `AVX2` para calcular distâncias euclidianas quadradas em apenas dois ciclos de CPU por vetor, garantindo alta performance mesmo com varredura completa (*brute-force* exato para 100% de *recall*).
- **Zero-Allocation**: Processamento via `Span<T>` e `stackalloc` para evitar alocações no *hot path* das requisições.

## Arquitetura

- cliente → nginx:9999 (round-robin) → api1 / api2

- Cada instância de API mantém o índice em memória mapeada.
- **Quantização int16**: As 14 dimensões (mais 2 de padding) são quantizadas em `int16`, reduzindo a banda de memória necessária pela metade comparado a `float32`, o que é crítico para manter o *throughput* dentro do limite de CPU.

## Submissão

Para submeter o seu projeto:

1. **Prepare o repositório:** Certifique-se de que o seu código está na branch principal (ou na branch que você pretende nomear como `submission`). O repositório **deve ser público**.
2. **Tag de submissão:** Verifique se o seu `docker-compose.yml` está funcional e se não há dependências externas (como bancos de dados ou cache) que não sejam nativas à imagem Docker.
3. **Formulário oficial:** Acesse o formulário de submissão fornecido pela organização da Rinha de Backend 2026.
4. **URL do repositório:** Forneça a URL do seu repositório público no GitHub (ex: `https://github.com/seu-usuario/rinha-backend-2026`).
5. **Verificação final:** A organização irá clonar o repositório, executar o `docker compose up --build` e disparar os testes de carga automatizados. Certifique-se de que o `Dockerfile` está configurado para realizar o pré-processamento dos dados conforme o esperado.

> **Dica:** O ambiente oficial de teste é Linux bare-metal. A performance que você vê localmente (via Docker Desktop/WSL) será significativamente superior no ambiente de submissão, devido à ausência da camada de virtualização.