# Contexto — Rinha de Backend 2026: Detecção de Fraude

## O que é

Competição de performance onde o objetivo é construir uma API de **detecção de fraude em transações de cartão via busca vetorial**. Pontuação = latência (p99) + qualidade de detecção.

---

## O que deve ser implementado

### Endpoints (porta `9999`)

| Método | Rota | Descrição |
|--------|------|-----------|
| `GET` | `/ready` | Retorna `2xx` quando a API está pronta |
| `POST` | `/fraud-score` | Recebe transação, retorna `{ approved, fraud_score }` |

### Lógica de decisão (4 passos)

1. **Vetorizar** o payload em 14 dimensões (normalização definida abaixo)
2. **Buscar** os 5 vetores mais próximos no dataset de referência (3M registros)
3. **Calcular** `fraud_score = fraudes_entre_os_5 / 5`
4. **Responder** `approved = fraud_score < 0.6`

---

## As 14 dimensões do vetor

| idx | nome | fórmula |
|-----|------|---------|
| 0 | `amount` | `clamp(amount / 10000)` |
| 1 | `installments` | `clamp(installments / 12)` |
| 2 | `amount_vs_avg` | `clamp((amount / customer.avg_amount) / 10)` |
| 3 | `hour_of_day` | `hora_UTC / 23` |
| 4 | `day_of_week` | `dia_semana / 6` (seg=0, dom=6) |
| 5 | `minutes_since_last_tx` | `clamp(minutos / 1440)` ou **`-1`** se `last_transaction: null` |
| 6 | `km_from_last_tx` | `clamp(km_from_current / 1000)` ou **`-1`** se `last_transaction: null` |
| 7 | `km_from_home` | `clamp(km_from_home / 1000)` |
| 8 | `tx_count_24h` | `clamp(tx_count_24h / 20)` |
| 9 | `is_online` | `1` ou `0` |
| 10 | `card_present` | `1` ou `0` |
| 11 | `unknown_merchant` | `1` se merchant não está em `known_merchants`, senão `0` |
| 12 | `mcc_risk` | valor de `mcc_risk.json` (padrão `0.5`) |
| 13 | `merchant_avg_amount` | `clamp(merchant.avg_amount / 10000)` |

> `clamp(x)` = manter em `[0.0, 1.0]`. `-1` é sentinela válido apenas nos índices 5 e 6.

---

## Arquivos de referência (pré-carregar no startup)

| Arquivo | Tamanho | Uso |
|---------|---------|-----|
| `resources/references.json.gz` | ~16 MB / 284 MB descomprimido | 3M vetores `{ vector[14], label }` com `"fraud"` ou `"legit"` |
| `resources/mcc_risk.json` | <1 KB | Score de risco por MCC (`0.0`–`1.0`) |
| `resources/normalization.json` | <1 KB | Constantes de normalização |

---

## Restrições de infraestrutura

- `docker-compose.yml` com imagens públicas `linux-amd64`
- **1 CPU + 350 MB de RAM** total entre todos os serviços
- Mínimo: 1 load balancer (round-robin, sem lógica de negócio) + 2 instâncias de API
- Rede: `bridge` (sem `host` ou `privileged`)
- API responde na porta `9999`

---

## Pontuação

- **Latência (`score_p99`)**: -3000 a +3000 baseado no p99 (satura em 1 ms = +3000; >2000 ms = -3000)
- **Detecção (`score_det`)**: -3000 a +3000; penaliza falsos negativos > falsos positivos > erros HTTP; >15% falhas = -3000
- **Total**: soma dos dois componentes (-6000 a +6000)

---

## Decisões críticas de performance

- O dataset tem 3M vetores — a busca KNN precisa ser rápida (estrutura de índice, SIMD, etc.)
- Os arquivos de referência não mudam → pré-processar e indexar no startup
- Duas instâncias stateless compartilham o mesmo problema de memória: 284 MB de vetores > limite de RAM disponível → avaliar compressão, quantização ou indexação aproximada (HNSW, LSH, etc.)
