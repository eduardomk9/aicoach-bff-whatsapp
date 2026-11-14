using System.Text.RegularExpressions;

namespace AiCoachBff.Api.Services;

/// <summary>
/// Orquestração da mensagem do WhatsApp (R20 F2): fila por usuário (UserGate) → dedupe →
/// lookup telefone → usuário conhecido espera o coach (send-message bloqueante) e responde;
/// telefone novo cria o LEAD pendente de pagamento e responde onboarding (a IA NUNCA é
/// chamada); texto com link(s) de mídia vira documento/imagem nativa (um anexo por link,
/// caption no 1º, degradando para texto em falha); falha da API vira mensagem amigável.
/// Processa EM BACKGROUND (o ack da Meta volta <10s — a espera do LLM não segura HTTP).
/// </summary>
public sealed class WebhookService(
    DedupeCache dedupe,
    UserGate userGate,
    IAiCoachClient aiCoach,
    IMetaClient meta,
    BffOptions bff,
    ILogger<WebhookService> logger)
{
    /// <summary>Enfileira o processamento (fire-and-forget; exceção logada, nunca propagada).
    /// O ack da Meta volta &lt;10s — a espera do LLM (~3min) nunca segura o HTTP.
    /// O fluxo entra na FILA POR USUÁRIO (DeliveryAsync) antes do processamento.</summary>
    public void Enqueue(InboundMessage message, string? profileName)
    {
        _ = Task.Run(() => DeliveryAsync(message, profileName));
    }

    /// <summary>
    /// Serializa o fluxo por waId (UserGate): rajada do WhatsApp — "treino de hj" seguido
    /// de "e a dieta?" segundos depois — bate dentro da espera do coach, e dois
    /// send-message do MESMO userId em paralelo corrompem a sessão (insert duplicado,
    /// corpus fora de ordem, estado dos fluxos misturado). Um turno de cada vez, na ordem.
    /// </summary>
    private async Task DeliveryAsync(InboundMessage message, string? profileName)
    {
        var gate = userGate.GetGate(PhoneNormalizer.Canonical(message.From));
        await gate.WaitAsync();
        try
        {
            await HandleAsync(message, profileName);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Processa a mensagem (o Enqueue chama em Task; o teste chama direto).</summary>
    public async Task HandleAsync(InboundMessage message, string? profileName)
    {
        var waId = PhoneNormalizer.Canonical(message.From);
        try
        {
            if (!dedupe.TryAcquire(message.Id))
            {
                logger.LogWarning("Mensagem {MessageId} repetida (reentrega da Meta) — ignorada.", message.Id);
                return;
            }

            if (!string.Equals(message.Type, "text", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(message.Text?.Body))
            {
                logger.LogInformation("Mensagem {MessageId} sem texto (type={Type}).", message.Id, message.Type ?? "?");
                await meta.SendTextAsync(waId, bff.NonTextMessage, CancellationToken.None);
                return;
            }

            await ReplyFromCoachAsync(waId, message.Text.Body, profileName);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Espera do coach estourou para {WaId} — resposta de fallback.", waId);
            await SendSilentlyAsync(waId, bff.FallbackErrorMessage);
        }
        catch (Exception exception)
        {
            // O usuário já não recebeu nada: resposta amigável + log completo.
            logger.LogError(exception, "Falha ao processar mensagem {MessageId} de {WaId}.", message.Id, waId);
            await SendSilentlyAsync(waId, bff.FallbackErrorMessage);
        }
    }

    private async Task ReplyFromCoachAsync(string waId, string text, string? profileName)
    {
        var userId = await aiCoach.LookupUserAsync(waId, CancellationToken.None);
        if (userId is null)
        {
            // Gate de leads (R20 F3): cadastro provisório pendente de pagamento;
            // onboarding sem IA — o coach não conversa com quem não é usuário.
            var leadId = await aiCoach.CreateLeadAsync(waId, profileName, CancellationToken.None);
            logger.LogInformation("Lead {LeadId} criado para {WaId} (perfil {Profile}).", leadId, waId, profileName ?? "(sem nome)");
            await meta.SendTextAsync(waId, bff.OnboardingMessage, CancellationToken.None);
            return;
        }

        var reply = await aiCoach.SendMessageAsync(userId.Value, text, CancellationToken.None);

        // Mídia nativa: o coach entrega PDF/foto com o link RELATIVO /chat/media/{id} —
        // baixa da API e entrega documento no WhatsApp (docs\whatsapp-pdf.md da API).
        // Multi-intenção (G19 da API): 2+ links no MESMO AgentMessage vira 2+ anexos —
        // entregar só o 1º deixaria o resto como texto relativo morto na conversa.
        var links = MediaLinkExtractor.FindAll(reply);
        if (links.Count > 0)
        {
            await DeliverMediaAsync(waId, reply, links);
            return;
        }

        await meta.SendTextAsync(waId, reply, CancellationToken.None);
    }

    private async Task DeliverMediaAsync(string waId, string reply, IReadOnlyList<string> links)
    {
        // Caption = a resposta INTEIRA do coach sem TODOS os links — só no PRIMEIRO anexo
        // (os demais entregam o artefato a seco). Espaços corridos colapsam; a estrutura
        // de parágrafos (\n\n) do relatório é preservada. O CapCaption do MetaClient
        // impõe o teto de 1024 do Graph na ponta.
        var captionBase = reply;
        foreach (var link in links)
        {
            captionBase = captionBase.Replace(link, " ", StringComparison.OrdinalIgnoreCase);
        }

        captionBase = Regex.Replace(captionBase, @"[ \t]+", " ");
        captionBase = Regex.Replace(captionBase, @"(\r?\n){3,}", "\n\n").Trim();
        // Teto do Graph (1024) imposto AQUI (a caption é do fluxo) e de novo na MetaClient
        // (última linha de defesa para qualquer chamador).
        var caption = string.IsNullOrWhiteSpace(captionBase) ? "Aqui está o seu arquivo." : MetaClient.CapCaption(captionBase)!;

        try
        {
            for (var i = 0; i < links.Count; i++)
            {
                var mediaId = MediaLinkExtractor.MediaIdOf(links[i]) ?? throw new InvalidOperationException($"Link de mídia sem id: {links[i]}");
                var media = await aiCoach.DownloadMediaAsync(mediaId, CancellationToken.None);
                var graphId = await meta.UploadMediaAsync(media.Content, media.ContentType, CancellationToken.None);

                var captionDoAnexo = i == 0 ? caption : null;
                if (media.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    await meta.SendImageAsync(waId, graphId, captionDoAnexo, CancellationToken.None);
                    continue;
                }

                var fileName = media.FileName ?? "aicoach.pdf";
                await meta.SendDocumentAsync(waId, graphId, fileName, captionDoAnexo, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            // Mídia falhou (id expirado/replica da API sem o arquivo, Graph fora, caption
            // rejeitada): o coach JÁ respondeu e o turno já avançou — o fallback geral
            // ("problema pra pensar") mentiria e apagaria a resposta paga. Degrada para o
            // TEXTO do coach sem os links (o link relativo não resolve no WhatsApp); se
            // não sobrou texto nenhum, frase honesta de arquivo.
            logger.LogError(exception, "Entrega de mídia falhou para {WaId} — degradando para o texto do coach.", waId);
            var texto = string.IsNullOrWhiteSpace(captionBase)
                ? "O arquivo não pôde ser entregue agora — me pede de novo que eu refaço."
                : captionBase;
            await SendSilentlyAsync(waId, texto);
        }
    }

    private async Task SendSilentlyAsync(string waId, string body)
    {
        try
        {
            await meta.SendTextAsync(waId, body, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Nem a resposta de fallback saiu (Graph indisponível): não há mais o que fazer — log.
            logger.LogError(exception, "Fallback também falhou para {WaId}.", waId);
        }
    }
}
