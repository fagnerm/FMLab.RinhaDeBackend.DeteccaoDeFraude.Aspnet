# TASKS

Ordem de execução definida por dependências: cada fase só começa quando a anterior estiver concluída.

---

## Fase 1 — Vetorização ✅
> Base de tudo. Sem vetor correto nada mais funciona.

- [x] Carregar constantes de `normalization.json` no startup (`ReferenceDataService`)
- [x] Carregar `mcc_risk.json` no startup; usar `0.5` como padrão para MCC ausente
- [x] Implementar `clamp(x)` para manter valores em `[0.0, 1.0]`
- [x] Implementar as 14 dimensões conforme fórmulas do CONTEXT.md
- [x] Tratar sentinela `-1` nos índices 5 e 6 quando `last_transaction: null`

---

## Fase 2 — Dataset e busca vetorial ✅
> Depende da vetorização para poder comparar vetores.

- [x] Descomprimir e carregar `references.json.gz` (3M vetores) em memória no startup (`VectorStore`)
- [x] Estratégia escolhida: quantização `byte` ([0,1]→[0,254], sentinel -1→255) — 42MB por instância vs 168MB com float32
- [x] Implementar busca KNN (k=5) por distância euclidiana quadrática em espaço quantizado
- [x] Calcular `fraud_score = fraudes_entre_os_5 / 5`
- [x] Aplicar threshold: `approved = fraud_score < 0.6`

---

## Fase 3 — Endpoints ✅
> Depende da busca vetorial para retornar resultado correto.

- [x] `POST /fraud-score` — receber payload, executar vetorização + KNN, retornar `{ approved, fraud_score }`
- [x] `GET /ready` — retornar `2xx` somente após o dataset estar carregado em memória

---

## Fase 4 — Infraestrutura ✅
> Pode ser preparada em paralelo com as fases anteriores, mas só é validada com a API funcional.

- [x] `docker-compose.yml` com 2 instâncias de API + nginx round-robin (sem lógica de negócio)
- [x] API exposta na porta `9999`
- [x] Soma de limites: 0.45+0.45+0.10=1.0 CPU, 160+160+30=350MB RAM — exatamente no limite
- [x] Imagens `linux-amd64`, rede `bridge`
- [x] Dockerfile corrigido (sem espaços à esquerda, build em 2 stages, sem intermediate build artifacts)
- [x] `.dockerignore` excluindo `bin/`, `obj/`, `references.json` (284MB) — só configs pequenas entram na imagem
- [x] `references.json.gz` montado via volume em `/app/App_Data/references.json.gz`
- [x] `VectorStoreLoader` resiliente — falha no carregamento não derruba o host; `/ready` retorna 204

---

## Fase 5 — Performance
> Só faz sentido depois que a solução está correta e rodando em container.

- [ ] Medir p99 de latência com carga real
- [ ] Medir uso de memória com as duas instâncias carregando o dataset simultaneamente
- [ ] Otimizar busca vetorial se necessário (SIMD, paralelismo, índice aproximado)
- [ ] Garantir p99 < 2000 ms (acima disso = score de latência -3000)

---

## Fase 6 — Submissão
> Última fase, após tudo validado.

- [ ] Branch `main` com código-fonte
- [ ] Branch `submission` com apenas `docker-compose.yml` e arquivos necessários para rodar
