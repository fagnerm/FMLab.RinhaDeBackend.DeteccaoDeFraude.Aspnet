# Rinha de Backend 2026 - Detecção de Fraude

Backend para a [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026), edição de detecção de fraude.

**Stack:** .NET 10 (AOT) · HAProxy · Unix sockets

## Como funciona

Classificação por **ANN (Approximate Nearest Neighbor)** via IVF (Inverted File Index):

1. 3M vetores de referência são agrupados em 500 clusters por K-means (class-separated fraud/legit)
2. A query é quantizada (float → byte) e comparada aos centroides via **AVX2/SSE2**
3. Os 5 clusters mais próximos são varridos (~30K comparações vs 3M no KNN exato)
4. Votação entre os 5 vizinhos encontrados → score de fraude

## Arquitetura

```
Cliente → HAProxy (TCP :9999) → api1 / api2 (Unix socket)
```

Dois processos ASP.NET Core compilados em AOT, comunicando com HAProxy via Unix domain sockets.

## Endpoints

| Método | Path | Descrição |
|--------|------|-----------|
| POST | `/fraud-score` | Classificação de transação |
| GET | `/ready` | Health check (aguarda índice carregar) |

## Rebuildar o índice

```sh
dotnet run --project src/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api -- --build-index
```

## Rodar localmente

```sh
docker compose up --build -d
```
