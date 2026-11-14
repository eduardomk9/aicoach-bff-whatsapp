using System.Text.RegularExpressions;

namespace AiCoachBff.Api.Services;

/// <summary>
/// Orquestração da mensagem do WhatsApp (R20 F2): dedupe → lookup telefone → usuário
/// conhecido espera o coach (send-message bloqueante) e responde; telefone novo cria
/// o LEAD pendente de pagamento e responde onboarding (a IA NUNCA é chamada); texto
/// com link de mídia vira documento/imagem nativa; falha da API vira mensagem amigável.
/// Processa EM BACKGROUND (o ack da Meta volta <10s — a espera do LLM não segura HTTP).
/// </summary>
public sealed class WebhookService(
    DedupeCache dedupe,
    IAiCoachClient aiCoach,
    IMetaClient meta,
    BffOptions bff,
    ILogger<WebhookService> logger)
{
    /// <summary>Enfileira o processamento (fire-and-forget; exceção logada, nunca propagada).
    /// O ack da Meta volta &lt;10s — a espera do LLM (~3min) nunca segura o HTTP.</summary>
    public void Enqueue(InboundMessage message, string? profileName)
    {
        _ = Task.Run(() => HandleAsync(message, profileName));
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
        var link = MediaLinkExtractor.Find(reply);
        if (link is { } mediaLink)
        {
            await DeliverMediaAsync(waId, reply, mediaLink);
            return;
        }

        await meta.SendTextAsync(waId, reply, CancellationToken.None);
    }

    private async Task DeliverMediaAsync(string waId, string reply, string mediaLink)
    {
        var mediaId = MediaLinkExtractor.MediaIdOf(mediaLink) ?? throw new InvalidOperationException($"Link de mídia sem id: {mediaLink}");
        var media = await aiCoach.DownloadMediaAsync(mediaId, CancellationToken.None);
        var graphId = await meta.UploadMediaAsync(media.Content, media.ContentType, CancellationToken.None);

        var semLink = Regex.Replace(reply.Replace(mediaLink, string.Empty, StringComparison.OrdinalIgnoreCase), @"\s+", " ").Trim();
        var caption = string.IsNullOrWhiteSpace(semLink) ? "Aqui está o seu arquivo." : semLink;

        if (media.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            await meta.SendImageAsync(waId, graphId, caption, CancellationToken.None);
            return;
        }

        var fileName = media.FileName ?? "aicoach.pdf";
        await meta.SendDocumentAsync(waId, graphId, fileName, caption, CancellationToken.None);
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
