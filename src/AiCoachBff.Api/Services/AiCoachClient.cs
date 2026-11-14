using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AiCoachBff.Api.Services;

public sealed record MediaDownload(byte[] Content, string ContentType, string? FileName);

/// <summary>Contrato do cliente da API AiCoach. Mockável nos testes.</summary>
public interface IAiCoachClient
{
    /// <summary>Id SQL do usuário pelo telefone da origem (wa_id canônico). Nulo = sem usuário (gate de lead).</summary>
    Task<int?> LookupUserAsync(string phone, CancellationToken cancellationToken);

    /// <summary>Registra o lead pendente de pagamento (idempotente pela API).</summary>
    Task<int> CreateLeadAsync(string phone, string? name, CancellationToken cancellationToken);

    /// <summary>Espera o coach (LLM bloqueante, até BFF:MaxWaitSeconds) e devolve o texto do coach.</summary>
    Task<string> SendMessageAsync(int userId, string message, CancellationToken cancellationToken);

    /// <summary>Baixa a mídia do link do chat (PDF do cronograma/relatório, foto da medida).</summary>
    Task<MediaDownload> DownloadMediaAsync(string mediaId, CancellationToken cancellationToken);
}

/// <summary>
/// Cliente da API AiCoach (R20 F2). Autentica como conta de SERVIÇO (seed
/// bff@aicoach.internal, perfil SERVICE) e fala os endpoints do BFF: GET /users/lookup,
/// POST /users/leads (gate, R20 F3) e POST /chat/send-message (bloqueante — o HttpClient
/// de chat tem timeout MaxWaitSeconds + folga; login/lookup correm no client curto).
/// Token JWT em cache com expiração.
/// </summary>
public sealed class AiCoachClient : IAiCoachClient
{
    public const string HttpClientName = "aicoach";
    public const string ChatHttpClientName = "aicoach-chat";

    private readonly IHttpClientFactory _factory;
    private readonly AiCoachOptions _options;
    private readonly ILogger<AiCoachClient> _logger;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public AiCoachClient(IHttpClientFactory factory, AiCoachOptions options, ILogger<AiCoachClient> logger)
    {
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    public async Task<int?> LookupUserAsync(string phone, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        var client = _factory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/users/lookup?phone={Uri.EscapeDataString(phone)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ResultEnvelope<UserLookupData>>(Json(), cancellationToken);
        return body?.Data?.UserId ?? throw new InvalidOperationException("Lookup sem userId na resposta.");
    }

    public async Task<int> CreateLeadAsync(string phone, string? name, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        var client = _factory.CreateClient(HttpClientName);

        var payload = new { phone, name };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users/leads")
        {
            Content = JsonContent.Create(payload, null, Json()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 201 (criado) e 200 (já existente) são as duas saídas do gate idempotente.
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ResultEnvelope<LeadData>>(Json(), cancellationToken);
        _logger.LogInformation("Lead registrado: {LeadId} (created={Created}).", body?.Data?.LeadId, body?.Data?.Created);
        return body?.Data?.LeadId ?? throw new InvalidOperationException("POST /users/leads sem LeadId na resposta.");
    }

    public async Task<string> SendMessageAsync(int userId, string message, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        var client = _factory.CreateClient(ChatHttpClientName);

        // O contrato do send-message espera userId COMO STRING no JSON (régua da API).
        var payload = new { userId = userId.ToString(), message, metadata = new { source = "whatsapp" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/chat/send-message")
        {
            Content = JsonContent.Create(payload, null, Json()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ResultEnvelope<AgentMessageData>>(Json(), cancellationToken);
        var reply = body?.Data?.AgentMessage;
        if (string.IsNullOrWhiteSpace(reply))
        {
            throw new InvalidOperationException("send-message sem agentMessage.");
        }

        _logger.LogInformation("Coach respondeu ({Length} chars).", reply.Length);
        return reply;
    }

    public async Task<MediaDownload> DownloadMediaAsync(string mediaId, CancellationToken cancellationToken)
    {
        var client = _factory.CreateClient(ChatHttpClientName);

        // /chat/media é anônimo hoje (TODO da API: autorizar quando o front real sair);
        // com o token é inofensivo e sobrevive à ativação do [Authorize].
        var token = await GetTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/chat/media/{mediaId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/pdf";
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;

        _logger.LogInformation("Mídia {MediaId} baixada ({Bytes} bytes, {Type}).", mediaId, content.Length, contentType);
        return new MediaDownload(content, contentType, fileName?.Trim('"'));
    }

    private JsonSerializerOptions Json() => new()
    {
        // A API fala camelCase (JsonOptions do ASP.NET) — parse case-insensitive
        // resolve; os outbound do client já declaram chaves literais corretas.
        PropertyNameCaseInsensitive = true,
    };

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } cached && DateTimeOffset.UtcNow < _tokenExpiry)
            {
                return cached;
            }

            var client = _factory.CreateClient(HttpClientName);
            // Sem Authorization na resposta do envelope: {success, data:{accessToken, email, name}}.
            var payload = new { email = _options.ServiceEmail, password = _options.ServicePassword };
            using var response = await client.PostAsJsonAsync("/auth/login", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<ResultEnvelope<AuthData>>(Json(), cancellationToken);
            _token = body?.Data?.AccessToken ?? throw new InvalidOperationException("Login do serviço sem token.");
            // Token da API vive horas; renovação com margem de 10 min é suficiente.
            _tokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
            _logger.LogInformation("Login de serviço renovado.");
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private sealed record ResultEnvelope<T>(bool Success, T Data);
    private sealed record UserLookupData(int UserId);
    private sealed record LeadData(int LeadId, bool Created);
    private sealed record AgentMessageData(string AgentMessage);
    private sealed record AuthData(string AccessToken, string Email, string Name);
}
