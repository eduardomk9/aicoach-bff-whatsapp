namespace AiCoachBff.Api.Services;

/// <summary>Config do WhatsApp Cloud API (Meta). Segredos chegam por env/secretref (Container Apps) —
/// o appsettings.json carrega só defaults não sensíveis.</summary>
public sealed class MetaOptions
{
    public const string Section = "Meta";

    /// <summary>Versão do Graph API (ex.: "v23.0").</summary>
    public string ApiVersion { get; init; } = "v23.0";

    /// <summary>Phone number ID do número do negócio (Cloud API).</summary>
    public string PhoneNumberId { get; init; } = string.Empty;

    /// <summary>Access token permanente (System User). NUNCA em arquivo versionado.</summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>Verify token acordado com o console da Meta (GET /webhook).</summary>
    public string WebhookVerifyToken { get; init; } = string.Empty;

    /// <summary>App Secret para validar X-Hub-Signature-256. Vazio = checagem desligada (dev).</summary>
    public string AppSecret { get; init; } = string.Empty;
}

/// <summary>Config da API do AiCoach à qual o BFF fala (ingress internal na nuvem).</summary>
public sealed class AiCoachOptions
{
    public const string Section = "AiCoach";

    /// <summary>Base URL da API (na nuvem: http://aicoach-api.internal.…azurecontainerapps.io).</summary>
    public string BaseUrl { get; init; } = "http://localhost:5105";

    /// <summary>Conta de serviço seedada no SQL (perfil SERVICE).</summary>
    public string ServiceEmail { get; init; } = "bff@aicoach.internal";

    /// <summary>Senha da conta de serviço. NUNCA em arquivo versionado.</summary>
    public string ServicePassword { get; init; } = string.Empty;
}

/// <summary>Canal demo (R20 — front provisório mobile-first /demo). AccessKey é a trava
/// do link público (?k=...); vazia em Production = endpoint INEXISTENTE (fail-closed).
/// Não é segredo caro — não abre API nem Meta, só o canal de testes.
/// R20 ADMIN: quem já é usuário conversa conforme Users.IsAllowed (estado no banco,
/// ddl-004) — a lista de env de allowlist MORREU (over-engineering, decisão do dono).</summary>
public sealed class DemoOptions
{
    public const string Section = "Demo";

    /// <summary>Chave que o visitante carrega no link (header X-Demo-Key).</summary>
    public string AccessKey { get; init; } = string.Empty;
}

/// <summary>Comportamento do BFF (espera do LLM, dedupe e mensagens de resposta fixa).</summary>
public sealed class BffOptions
{
    public const string Section = "BFF";

    /// <summary>Esperança máxima na resposta do send-message (LLM 20-120s, pior caso ~3min).</summary>
    public int MaxWaitSeconds { get; init; } = 180;

    /// <summary>Tamanho do cache de dedupe (id da mensagem da Meta, LRU).</summary>
    public int DedupeCacheSize { get; init; } = 1000;

    /// <summary>Resposta fixa ao telefone sem usuário (gate de leads — a IA NUNCA é chamada).</summary>
    public string OnboardingMessage { get; init; } =
        "Olá! Eu sou o coach do AiCoach 💪 Encontrei o seu WhatsApp, mas ainda não há conta com este número. " +
        "Te registrei como aluno pendente de confirmação — já vamos falar com você!";

    /// <summary>Resposta fixa a mensagem não textual (áudio/imagem — extrator entrante é follow-up).</summary>
    public string NonTextMessage { get; init; } =
        "Por enquanto só entendo mensagens de texto — manda aí escrito! 💬";

    /// <summary>Resposta fixa (só no canal demo) a número JÁ CADASTRADO fora da
    /// allowlist da demo — o cliente real conversa pelo WhatsApp oficial; a sessão
    /// dele não pode ser tocada pelo provisório.</summary>
    public string KnownUserDemoMessage { get; init; } =
        "Esse número já tem coach — a conversa dele é só pelo WhatsApp oficial. 😊";

    /// <summary>Resposta fixa quando a API/LLM falha (503, timeout) — o usuário nunca fica sem resposta.</summary>
    public string FallbackErrorMessage { get; init; } =
        "Tive um problema pra pensar agora — tenta me mandar de novo em instantes, por favor! 🏋️";
}
