# AiCoachBff — o canal do AiCoach

Azure Container App em .NET 8 (Clean Arch) que conecta **WhatsApp (Meta Cloud API)** e a
**demo web** ao motor (`aicoach-api`). Não decide nada de negócio: recebe, resolve quem é o
usuário e entrega.

## O desenho (este repo)

```mermaid
flowchart LR
    WA["WhatsApp<br/>(Meta Cloud API)"] -- "POST /webhook<br/>(assinatura X-Hub)" --> HOOK["WebhookController"]
    DEMO["Demo web<br/>(wwwroot/index.html)"] -- "POST /demo/message" --> DEMOC["DemoController"]
    HOOK --> WS["WebhookService<br/>(fila por usuário, IOutboundSink)"]
    WS -- "lookup + send-message" --> API["aicoach-api<br/>(o motor)"]
    WS -- "responde por texto" --> SINK["MetaSink (IMetaClient)"]
    DEMOC -- "só anota (demo)" --> CS["CollectorSink"]
    SINK -- "POST graph.facebook.com/.../messages" --> META["Meta WhatsApp"]
    API -.->|GET /users/lookup (perfil SERVICE)| LK["resolve id SQL pelo telefone"]
```

- **Rota WhatsApp**: webhook da Meta (verificação GET + mensagens POST com validação de
  assinatura) → fila por usuário (in-memory) → `WebhookService` → resolve o **id SQL** pelo
  telefone (`GET /users/lookup?phone=` restrito ao perfil SERVICE) → `send-message` na API →
  a resposta volta por `IMetaClient` (texto/documento/imagem com split e caption).
- **Rota demo**: `/demo/message` fala com a MESMA esteira, mas o sink é o `CollectorSink`
  (a demo só anota — a demo NUNCA conversa com a Meta).
- **Quem envia hoje**: só respostas a mensagens recebidas (a janela de 24h da Meta, free-form).
- **Fase 2 (R22)**: endpoint planejado `POST /outbound/notify` — o job de notificações
  (`aicoach-notifications`) chama AQUI e o BFF é o ÚNICO dono do segredo da Meta. Template
  aprovado com corpo 100% variável para o envio fora da janela (nunca 1 template por usuário).

## As PARAMETRIZAÇÕES (config/secrets do Container App)

| Knob | Onde | Padrão | Para que; efeito |
|---|---|---|---|
| `Meta__PhoneNumberId` + `meta-access-token` | ACA secrets | — | o número do WhatsApp que ENVIA. Trocar o número/billing = trocar aqui (o PATCH leva a lista INTEIRA) |
| `Meta__WebhookToken`, `Meta__AppSecret` | ACA secrets | — | a validação da assinatura do webhook (sem isso a Meta não entra) |
| `Meta__ApiVersion` | ACA env | `v23.0` | a versão da Graph API (upgrade consciente) |
| `AiCoach__BaseUrl` | ACA env | o FQDN https interno da API | para onde o BFF fala (https — o ingress http→https vira POST 405) |
| `AiCoach__ServiceEmail/Password` | ACA env+secret | `bff@aicoach.internal` | o usuário de SERVIÇO (perfil SERVICE) que resolve o lookup |
| `Demo__AllowedPhones__0..` | ACA env | — | a allowlist da demo (quem entra pela página publica) |
| `demo-access-key` | ACA secret | — | a chave da URL da demo (`?k=...`) |

**A régua do PATCH**: qualquer PATCH de `configuration` do Container App substitui a lista
**INTEIRA** (secrets + ingress + registries vão JUNTOS — o parcial amputou o ingress por horas
uma noite). O passo a passo do deploy e do Meta: `BackEnd\docs\bff\DEPLOY.md` e
`bff\meta-whatsapp-setup.md`.

## Estrutura

```
src/AiCoachBff.Api          — controllers (webhook, demo), serviços (WebhookService, MetaClient, Outbound)
src/AiCoachBff.Tests
```

## Rodando

```bash
dotnet build AiCoachBff.sln
dotnet test -c Release
```

Deploy: **push em master** — o pipeline roda os testes e troca a imagem no ACA
`aicoach-bff` (external, min 1). Logs da fundação: `docs\bff\diag-logs.md` (o workflow
`diag-logs` puxa logs por commit quando a rede local morre).

## Status

- Em produção (a chave da demo: `?k=...`); Meta em modo sandbox até o F7 (número aprovado,
  webhook de produção). O envio real de notificações (proativo) é da fase 2, acima.
