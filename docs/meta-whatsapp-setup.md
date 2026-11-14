# WhatsApp da Meta — guia de cliques (R20 F7)

Guia pra quem nunca viu essa tela. Sem programação: é tudo clicar no site da Meta e colar
2 valores no Azure depois. Ordem MATTER.

Termos primeiro:

| Termo | O que é | Onde |
|---|---|---|
| **App da Meta** | O "programa" no Facebook que conecta com o WhatsApp | developers.facebook.com |
| **WABA** | WhatsApp Business Account — a conta do número empresarial | business.facebook.com |
| **Phone number ID** | O ID do NÚMERO de WhatsApp (o BFF fala com ele) | App → WhatsApp → API Setup |
| **App Secret** | Senha do APP (o BFF usa pra assinar o webhook) | App → App settings → Basic |
| **Token (System User)** | Senha do BOT pra mandar mensagem | business.facebook.com/settings |
| **Verify token** | String aleatória QUE VOCÊ INVENTA (só pro handshake do webhook) | você cria |

## 1. Criar o App (10 min)

1. https://developers.facebook.com → login com a conta pessoal do dono.
2. **My Apps → Create App → Other** (não é "Consumer") → tipo **Business**.
3. Nome: `AiCoach WhatsApp` → e-mail → **Create app**. A Meta já cria a WABA junto.
4. Dentro do app, página "Add products" → achar **WhatsApp** → **Set up**.
5. Menu lateral → **WhatsApp → Quickstart / API Setup**: copie o **Phone number ID**
   (número de TESTE, free, já criado pela Meta). Anote — vai no comando do BFF (DEPLOY.md,
   passo 9) como `Meta__PhoneNumberId`.
6. **App settings → Basic**: copie o **App Secret** → vai no secret `meta-app-secret`.

## 2. Smoke test gratuito (5 min)

1. Em **API Setup → "Send a test message" / To**: adicione o número do WhatsApp PESSOAL do
   dono (formato DDI: `55DDDNNNNNNNNN`).
2. Gere um token TEMPORÁRIO (24h) no mesmo painel (só pro smoke — o token do BFF vem no 3)
   e mande uma mensagem de teste. O WhatsApp pessoal do dono **recebe** — o canal funciona.
3. NÃO configure nada no Azure com o token temporário (expira em 24h).

## 3. Token permanente (15 min)

1. https://business.facebook.com/settings → menu **System users** → **Add**:
   nome `aicoach-bot`, role **Admin**.
2. Detalhes do system user → **Assign assets** → o app `AiCoach WhatsApp`
   (**Full control**) + a WABA (**Full control**).
3. Botão **Generate token**: escopos `whatsapp_business_management` +
   `whatsapp_business_messaging`, expiração **Never**.
4. **O token aparece 1 ÚNICA VEZ** — copie na hora e coloque no secret do Azure:
   `meta-access-token` (comando de troca no `DEPLOY.md`, "Rotina de troca de token").

## 4. Apontar o webhook (10 min — precisa do BFF no Azure rodando)

1. **Invente** uma string aleatória longa (ex.: gerador de senha) → essa é a verify token:
   coloque como secret `meta-webhook-token` na BFF (e o BFF reiniciado).
2. App → **Webhooks** → seletor **WhatsApp** → botão **Configure**:
   - **Callback URL**: `https://<fqdn-do-bff>/webhook` (o fqdn aparece no Azure,
     `containerapp show`).
   - **Verify token**: a MESMA string do secret.
   - **Verify and save** → a Nossa API chama o GET `/webhook` e devolve o challenge
     (se falhar, veja os logs da BFF: `az containerapp logs show -n aicoach-bff -g rg-aicoach`).
3. No mesmo painel, **Subscribe to field** → `messages`.
   (Status/delivery callbacks não são assinados — o BFF os ignora de propósito.)

## 5. Teste de verdade (o coach responde)

1. Manda o usuário "oi" do WhatsApp dele.
2. Número desconhecido → mensagem de onboarding (e vira lead pendente de pagamento — a IA
   NÃO é chamada). Número cadastrado (com DDI `55DDD…` no `Users.Phone`) → resposta do
   coach em até ~3min na 1ª vez (SQL cold-start; depois ~10–30s).
3. Logs em tempo real: `az containerapp logs show -n aicoach-bff -g rg-aicoach --tail 100`.

## 6. Produção (SOMENTE depois do smoke tudo verde)

1. WhatsApp Manager (business.facebook.com/whatsapp) → **Phone numbers → Add phone number**
   → número REAL empresarial que **não tem WhatsApp pessoal** (se for celular com WhatsApp,
   é preciso desinstalar/desligar o WhatsApp dele antes — a Meta barrou).
2. Verificação do número: por **CALL** com o PIN que o painel dá (SMS pro SIM virtual é o
   plano B). SIM virtual próprio (comum no Brasil) resolve.
3. Subir `Meta__PhoneNumberId` no Azure pra o número novo
   (`az containerapp update ... --set-env-vars` ou secret/env da BFF conforme o runbook) e
   reiniciar. O `Meta__PhoneNumberId` do número de teste é abolido.
4. **Business não verificado**: a Meta limita ~250 conversas/24h — suficiente pro smoke e
   primeiros clientes; verificação de business (selo) é follow-up com CNPJ/documentos.
5. Nesta fase TODO fluxo é free-form DENTRO de janela 24h (COST ZERO de Meta): templates
   proativos (cobrança, lembrete fora da janela) = follow-up com cobrança por conversa.
