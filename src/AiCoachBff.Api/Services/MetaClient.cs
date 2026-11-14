using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AiCoachBff.Api.Services;

/// <summary>Contrato do cliente Graph (WhatsApp Cloud API). Mockável nos testes.</summary>
public interface IMetaClient
{
    /// <summary>Manda mensagem de texto (divide se passar de 4096 chars).</summary>
    Task SendTextAsync(string waId, string body, CancellationToken cancellationToken);

    /// <summary>Manda documento nativo (PDF) pelo media_id já subido. Vazio no arquivo.</summary>
    Task SendDocumentAsync(string waId, string mediaId, string fileName, string caption, CancellationToken cancellationToken);

    /// <summary>Manda imagem nativa pelo media_id já subido.</summary>
    Task SendImageAsync(string waId, string mediaId, string caption, CancellationToken cancellationToken);

    /// <summary>Sube bytes de mídia e devolve o media_id do Graph.</summary>
    Task<string> UploadMediaAsync(byte[] content, string contentType, CancellationToken cancellationToken);
}

/// <summary>
/// Cliente do WhatsApp Cloud API (Meta) — R20 F2, caminho direto sem BSP
/// (docs\whatsapp-pdf.md da API fixa o contrato: POST /{PHONE_NUMBER_ID}/messages;
/// mídia vai primeiro no POST /{PHONE_NUMBER_ID}/media e o document fala media_id).
/// Janela de 24h: toda resposta do coach segue uma mensagem recebida do usuário —
/// free-form sempre aberta; template proativo é follow-up.
/// </summary>
public sealed partial class MetaClient : IMetaClient
{
    public const string HttpClientName = "meta";

    // WhatsApp text body aceita até 4096 chars por mensagem (Graph rejeita acima).
    private const int MaxTextLength = 4096;

    private readonly HttpClient _http;
    private readonly MetaOptions _meta;
    private readonly ILogger<MetaClient> _logger;

    public MetaClient(HttpClient http, MetaOptions meta, ILogger<MetaClient> logger)
    {
        _http = http;
        _meta = meta;
        _logger = logger;
    }

    private Uri MessagesUrl =>
        new($"https://graph.facebook.com/{_meta.ApiVersion}/{_meta.PhoneNumberId}/messages");

    private JsonSerializerOptions Json() => new()
    {
        // Outbound sem policy: os payloads anonymous já declaram as chaves em
        // snake_case literal (messaging_product, preview_url, recipient_type).
        PropertyNamingPolicy = null,
    };

    public async Task SendTextAsync(string waId, string body, CancellationToken cancellationToken)
    {
        // Mensagens de resposta do coach podem passar de 4096? O relatório resume; mas
        // o guardrail: dividir em blocos de no máximo o limite antes de mandar.
        var blocos = SplitText(body);
        foreach (var bloco in blocos)
        {
            var payload = new { messaging_product = "whatsapp", recipient_type = "individual", to = waId, type = "text", text = new { preview_url = false, body = bloco } };
            await PostJsonAsync(payload, cancellationToken);
        }
    }

    public async Task SendDocumentAsync(string waId, string mediaId, string fileName, string caption, CancellationToken cancellationToken)
    {
        var payload = new { messaging_product = "whatsapp", to = waId, type = "document", document = new { id = mediaId, filename = fileName, caption = caption } };
        await PostJsonAsync(payload, cancellationToken);
    }

    public async Task SendImageAsync(string waId, string mediaId, string caption, CancellationToken cancellationToken)
    {
        var payload = new { messaging_product = "whatsapp", to = waId, type = "image", image = new { id = mediaId, caption = caption } };
        await PostJsonAsync(payload, cancellationToken);
    }

    public async Task<string> UploadMediaAsync(byte[] content, string contentType, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "arquivo");
        form.Add(new StringContent("whatsapp"), "messaging_product");

        var url = new Uri($"https://graph.facebook.com/{_meta.ApiVersion}/{_meta.PhoneNumberId}/media");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _meta.AccessToken);

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<GraphMediaResponse>(Json(), cancellationToken);
        _logger.LogInformation("Upload de mídia ok ({Bytes} bytes, {Type}).", content.Length, contentType);
        return body?.Id ?? throw new InvalidOperationException("Graph não devolveu media_id no upload.");
    }

    /// <summary>
    /// A assinatura HMAC-SHA256 do body do webhook (X-Hub-Signature-256) com o App Secret —
    /// primeira parede do webhook: só a Meta consegue assinar.
    /// </summary>
    public static string Signature(byte[] body, string appSecret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private async Task PostJsonAsync(object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl)
        {
            Content = JsonContent.Create(payload, null, Json()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _meta.AccessToken);

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Mensagem entregue no Graph ({Status}).", (int)response.StatusCode);

        // Delay mínimo anti-explosão de ordem: blocos em sequência (o WhatsApp
        // entrega fora de ordem raramente; sem delay a resposta é igual).
    }

    private static IEnumerable<string> SplitText(string body)
    {
        if (body.Length <= MaxTextLength)
        {
            yield return body;
            yield break;
        }

        foreach (var chunk in body.Chunk(MaxTextLength))
        {
            yield return new string(chunk);
        }
    }

    private sealed record GraphMediaResponse(string Id);
}
