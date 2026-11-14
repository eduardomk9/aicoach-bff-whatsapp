using System.Text;
using System.Text.Json;
using AiCoachBff.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AiCoachBff.Api.Controllers;

/// <summary>
/// Webhook do WhatsApp Cloud API (R20 F2). GET = handshake de verificação da Meta
/// (hub.challenge devolvido texto puro); POST = entregas (ack 200 &lt;10s, processamento
/// em background). Primeira parede: X-Hub-Signature-256 HMAC-SHA256 do raw body com o
/// App Secret (desligada só enquanto AppSecret vier vazio em dev).
/// </summary>
[ApiController]
[Tags("Webhook")]
public sealed class WebhookController(
    WebhookService webhookService,
    IOptions<MetaOptions> metaOptions,
    ILogger<WebhookController> logger) : ControllerBase
{
    private const string SignatureHeaderName = "X-Hub-Signature-256";

    private JsonSerializerOptions InboundJson() => new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Handshake: hub.verify_token bate → devolve hub.challenge (texto puro, não JSON).</summary>
    [HttpGet("/webhook")]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (string.Equals(mode, "subscribe", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(challenge)
            && string.Equals(verifyToken, metaOptions.Value.WebhookVerifyToken, StringComparison.Ordinal))
        {
            return Content(challenge, "text/plain");
        }

        return StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>Entrega de eventos: assinatura → ack rápido → processa em background.</summary>
    [HttpPost("/webhook")]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (!SignatureOk(raw))
        {
            return Unauthorized();
        }

        WebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookPayload>(raw, InboundJson());
        }
        catch (JsonException exception)
        {
            // Payload não parseou: ack de qualquer forma (200 evita a Meta
            // re-entregar o mesmo pedaço ruim para sempre) + log pro operador.
            logger.LogWarning(exception, "Webhook da Meta com body não-JSON ({Length} bytes): ack sem processar.", raw.Length);
            return Ok("EVENT_RECEIVED");
        }

        if (payload?.Entry is null)
        {
            // Payload não reconhecido: ack de qualquer forma (a Meta não re-entrega
            // o que já recebeu 200 — dedupe cobre o resto).
            return Ok(new { @object = "whatsapp_business_account" });
        }

        foreach (var entry in payload.Entry)
        {
            foreach (var change in entry.Changes ?? [])
            {
                var value = change.Value;
                if (value?.Messages is not { Length: > 0 } messages)
                {
                    continue; // statuses / outros fields: ack e nada.
                }

                var profileName = value.Contacts?.Length > 0 ? value.Contacts[0].Profile?.Name : null;
                foreach (var message in messages)
                {
                    // Ack <10s: espera do LLM (~3min) NUNCA segura o HTTP — Task.
                    webhookService.Enqueue(message, profileName);
                }
            }
        }

        return Ok("EVENT_RECEIVED");
    }

    /// <summary>Assinatura obrigatória quando AppSecret está configurado; vazio = dev.
    /// O boot fail-closed (Program.cs) proíbe o vazio fora de Development — aqui fica
    /// só o ambiente de dev, e o Critical garante que NINGUÉM passe por cima sem saber.</summary>
    private bool SignatureOk(string raw)
    {
        var appSecret = metaOptions.Value.AppSecret;
        if (string.IsNullOrEmpty(appSecret))
        {
            logger.LogCritical("Assinatura do webhook DESLIGADA (Meta:AppSecret vazio) — POST aceito SEM verificação de identidade. Configure o App Secret antes de publicar.");
            return true;
        }

        if (!Request.Headers.TryGetValue(SignatureHeaderName, out var header))
        {
            return false;
        }

        // O hex esperado é lowercase — comparação ORDINAL.
        var expected = MetaClient.Signature(Encoding.UTF8.GetBytes(raw), appSecret);
        return string.Equals(header.ToString(), expected, StringComparison.Ordinal);
    }
}
