# E2E da demo (R20) — 2026-11-14

Executado contra a NUVEM (aicoach-bff.yellowpond-..., canal demo com X-Demo-Key).
Resultados brutos capturados em `e2e-resultados.txt` (gerado pelo driver) e
conservados no histórico do repo (commit diag da madrugada).

## Provas executadas

| prova | cenário | resultado |
|---|---|---|
| P1 | número desconhecido `5531900009988` → onboarding + row em `Leads` | ✅ onboarding sem IA + `PENDING_PAYMENT|whatsapp` (SQL readback) + limpeza (Leads=0) |
| P2 | dono `5541998778427` (allowlist) → coach real | ✅ resposta real do agent (sem texto de erro) |
| P5 | restart da revisão da API → fallback → recuperação | ✅ fallback amigável + coach respondendo de novo (`RECOVER=True`) |
| P3 | "manda o pdf" → documento nativo | 📋 META-GATED: exige webhook real (F7) |
| P4 | payload repetido → 1 resposta | 📋 META-GATED: dedupe mora na porta do webhook (ids reais); a demo usa `demo-{Guid}` sem reentrega (por desenho) |

## Caso da madrugada (causa raiz do 405)

1. PATCH parcial de `configuration` (só `secrets`) **amputou o `ingress`** da API —
   o front-end respondeu 405 a todo POST `/auth/login`.
2. O redirect http→https do ingress derrubava o POST para GET (Kestrel 405, corpo vazio).
3. **Restauração**: PATCH de `configuration` COMPLETO (secrets + ingress + registries)
   + `AiCoach__BaseUrl` em **https://**.
4. **Régua daqui em diante**: qualquer PATCH de `configuration` leva secrets + ingress
   + registries JUNTOS (listas substituídas inteiras).

## Instrumento que fechou o caso (reutilizável)

Workflow `diag-logs.yml`: push de `diag-logs.flag` → o runner (Azure↔Azure saudável)
puxa logs/tags/estado dos apps e COMMITA no repo → leitura local via `git pull`.
Zero dependência da rota local→Azure (que morre em rajadas de 10054).
