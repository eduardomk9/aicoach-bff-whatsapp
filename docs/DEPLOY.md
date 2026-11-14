# DEPLOY — BFF do WhatsApp

Runbook do BFF (R20). A fundação (resource group, ACR, SQL, schema, SPN, environment e o
app da API interna) está em `AiCoach\docs\DEPLOY.md` — este arquivo cobre O QUE É SÓ O BFF
e a integração com a Meta. Guia de cliques da Meta:
`AiCoachBff\docs\meta-whatsapp-setup.md`.

## Desenho

```
Página demo (web) ──POST /demo/message──▶ MESMA esteira (resposta coletada em JSON,
                                          anexos base64 inline)
WhatsApp ──webhook HTTPS──▶ aicoach-bff (Container App EXTERNAL, min 1)
                              ├─ GET /webhook  → handshake da Meta (texto puro)
                              ├─ POST /webhook → HMAC do App Secret; ack 200 <10s
                              │   dedupe → login SERVICE → /users/lookup
                              │     achou → /chat/send-message (espera ≤180s)
                              │     404  → /users/leads (gate) + onboarding, IA NUNCA
                              └─ link /chat/media/… no texto → baixa + upload → mídia nativa
```

## Rotina de deploy (dia a dia)

Push em `master` dispara a pipeline `deploy.yml`: testes → build da imagem (tag = sha) →
`az containerapp update --image` (image-swap — secrets/env intactos).

**Rollback**:

```bash
az containerapp update -g rg-aicoach -n aicoach-bff --image <ACR>.azurecr.io/aicoach-bff:<sha-antigo>
```

## Config 1x no GitHub

Settings → Secrets and variables → Actions (mesmos valores do repo da API):
- **Secret** `AZURE_CREDENTIALS` — JSON do SPN.
- **Variable** `ACR_NAME` — nome do ACR.

A 1ª execução fica vermelha até a fundação existir (fase 6–8 do DEPLOY da API).

## Criação do app (fase 9 do DEPLOY da API)

O BFF depende de `<FQDN_INTERNO_DA_API>` (comando do passo 9 do runbook da API).
Secrets ANTES das env `secretref:`:

- `meta-access-token` — token do System User da Meta (Never).
- `meta-webhook-token` — verify token, string aleatória criada AQUI e colada no console da Meta.
- `meta-app-secret` — App settings → Basic no developers.facebook.com.
- `aicoach-service-password` — senha do login SERVICE da BFF na API (o seed traz o hash).

Envs: `AiCoach__BaseUrl=http://<FQDN_INTERNO_DA_API>`, `AiCoach__ServiceEmail=bff@aicoach.internal`,
`Meta__ApiVersion=v23.0`, `Meta__PhoneNumberId=<PHONE_NUMBER_ID>`, `ASPNETCORE_ENVIRONMENT=Production`
+ os `secretref:` da tabela acima (o comando inteiro está no runbook da API).

## Canal demo (front provisório, R20)

Enquanto a conta da Meta não chega, `wwwroot/index.html` (chat com bolhas, mobile-first)
roda na raiz da própria BFF e fala com a MESMA esteira do webhook via `POST /demo/message`
(mesma fila por usuário, mesmo gate de leads, IA e mídia — zero drift com o canal oficial).

Extras do canal demo (secrets ANTES das env `secretref:`):

- `demo-access-key` — chave de acesso embutida no link (`?k=...`). Vazia em Production =
  o endpoint NEM EXISTE (fail-closed). Não é segredo caro — trava do canal de testes.
- `Demo__AccessKey` = `secretref:demo-access-key`
- `Demo__AllowedPhones__0=55<DDD+numero>` — números autorizados a falar com o COACH pela
  demo. Número novo sempre vira lead/onboarding (coração da demo); cliente real só passa
  se listado — sem a allowlist, quem tivesse o link conversava NA sessão real do cliente.
  O dono lista o próprio `55<DDD+numero>`.

Travas em vigor: 30 mensagens/15 min por IP (janela fixa), 3 turnos simultâneos no
processo inteiro, texto de até 2000 caracteres, corpo de request até 64 KB. Boot
fail-closed também exige força mínima (≥20 caracteres) quando a chave está configurada.

Resíduos aceitos do review (baixos, provisório): com o canal desligado a página ainda
abre e o primeiro envio recebe a frase honesta de canal desligado; turnos da demo que
tocam sessão real (só números na allowlist) entram na API com `source=whatsapp`
(follow-up: marcador de origem próprio).

## Rotina de troca de token da Meta

O token "Never" do System User raramente expira, mas troca-se quando a Meta invalidar
(ou quando o telefone de produção subir — novo token não é preciso, só o PhoneNumberId):

1. business.facebook.com/settings → System Users → `aicoach-bot` → Generate token
   (escopos `whatsapp_business_management` + `whatsapp_business_messaging`).
2. Colar o token novo (o console do app aparece 1×, guarde-o na hora):

```bash
az containerapp secret set -n aicoach-bff -g rg-aicoach --secrets meta-access-token="<NOVO_TOKEN>"
az containerapp revision restart -n aicoach-bff -g rg-aicoach
```

3. Provar: `curl https://<fqdn-do-bff>/healthz` + mensagem de teste no WhatsApp.

## Verificação do webhook

```bash
curl https://<fqdn-do-bff>/healthz
curl "https://<fqdn-do-bff>/webhook?hub.mode=subscribe&hub.verify_token=<VERIFY_TOKEN>&hub.challenge=echo123"
# corpo exatamente "echo123"
```

Depois de "Verify and save" no console da Meta com o MESMO verify token, a Meta passa a
mandar POSTs; cada mensagem do WhatsApp entra por ali com assinatura HMAC validada.

## Notas do desenho

- Ack <10s SEMPRE: o LLM (~3min) roda em Task fire-and-forget; exceção → mensagem de
  erro amigável pro usuário, nunca crash.
- Dedupe LRU in-memory (escopo por instância — max-replicas 2 aceito; fila durável = follow-up).
- Se o BFF reinicia no meio do processamento, a mensagem se perde (sem fila nesta fase).
- Número de conversa é free-form DENTRO da janela 24h — nenhum template cobrado nesta fase.
