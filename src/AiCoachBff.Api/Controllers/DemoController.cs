using AiCoachBff.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AiCoachBff.Api.Controllers;

/// <summary>
/// Canal demo (R20 — front provisório enquanto a conta da Meta não chega): a página
/// mobile-first fala com a MESMA esteira do webhook (fila por usuário, gate de
/// leads, coach, mídia) via SimulateAsync — resposta coletada volta como JSON com texto
/// e ARQUIVOS em BASE64 inline (o anexo já vem baixado da API pela esteira; nada de
/// URL dupla: com min 1 max 2 réplicas o segundo pedido cairia na outra réplica sem o
/// arquivo — resposta única é inerente à correção, não à conveniência).
/// Travas do provisório público: chave de acesso (header X-Demo-Key, vindo do link
/// ?k=; vazia em Production = desligada), janela fixa de rajada por IP e teto de turnos
/// simultâneos (o custo real é o LLM).
/// </summary>
[ApiController]
[Tags("Demo")]
public sealed class DemoController(
    WebhookService webhookService,
    DemoLimiter limiter,
    IOptions<DemoOptions> demoOptions,
    IHostEnvironment environment,
    ILogger<DemoController> logger) : ControllerBase
{
    private const int LimitMensagens = 30;
    private const int TetoTexto = 2000;
    private const int TetoNome = 100;
    private static readonly TimeSpan Janela = TimeSpan.FromMinutes(15);

    // Teto de turnos SIMULTÂNEOS (página em espera do LLM até ~3min): 3 em voo protege
    // o gateway do LLM do abuso de paralelismo. Estático = todo o processo.
    private static readonly SemaphoreSlim TurnosEmVoo = new(3, 3);

    public sealed record DemoRequest(string? Phone, string? Name, string? Text);

    public sealed record DemoTextReply(string Type, string Body);

    /// <summary>Anexo já baixado: Data base64 (blob no browser), ContentType pra saber
    /// se renderiza imagem ou vira download, Caption do turno (só no 1º anexo) e
    /// FileName pro download.</summary>
    public sealed record DemoFileReply(string Type, string FileName, string ContentType, string Caption, string Data);

    /// <summary>Um turno: entra texto, sai a sequência EXATA de itens que o WhatsApp
    /// receberia (texto e/ou anexos — na ordem da esteira).</summary>
    [HttpPost("/demo/message")]
    [RequestSizeLimit(64_000)]
    public async Task<IActionResult> Message([FromBody] DemoRequest request, CancellationToken cancellationToken)
    {
        // Rajada PRIMEIRO (review R20): adivinhar a chave custa quota de IP — quem testa
        // chaves em rajada morre no 429 antes de saber se errou.
        if (!limiter.Allow($"msg:{ClientId()}", DateTimeOffset.Now, LimitMensagens, Janela))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = "Muitas mensagens de uma vez — pausa 15 minutos e volta, por favor!" });
        }

        if (Gated() is { } gateResult)
        {
            return gateResult;
        }

        var waId = PhoneNormalizer.Canonical(request.Phone ?? string.Empty);
        if (waId.Length < 10)
        {
            return BadRequest(new { error = "Número incompleto — manda com DDD, no formato 5541999998888." });
        }

        if (waId.Length > 15)
        {
            return BadRequest(new { error = "Número inválido — confere DDI + DDD, no formato 5541999998888." });
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return BadRequest(new { error = "Mensagem vazia — escreve alguma coisa! 😉" });
        }

        if (request.Text.Length > TetoTexto)
        {
            return BadRequest(new { error = "Mensagem comprida demais — manda a ideia num pedaço! 😉" });
        }

        if (request.Name is { Length: > TetoNome })
        {
            return BadRequest(new { error = "Nome comprido demais — versão curta, por favor! 😉" });
        }

        if (!await TurnosEmVoo.WaitAsync(0, cancellationToken))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = "Tô no fim de uma conversa agora — manda de novo em instantes! 🏋️" });
        }

        try
        {
            var inbound = new InboundMessage(
                From: waId,
                Id: $"demo-{Guid.NewGuid():N}",
                Type: "text",
                Text: new InboundText(request.Text));

            var items = await webhookService.SimulateAsync(inbound, request.Name, cancellationToken);
            return Ok(new { replies = Map(items) });
        }
        finally
        {
            TurnosEmVoo.Release();
        }
    }

    /// <summary>Uma regra e três saídas: 404 quando a demo está DESLIGADA (production sem
    /// chave — o endpoint "não existe"), 403 quando a chave não bate, null quando livre.</summary>
    private IActionResult? Gated()
    {
        var access = DemoAuthorization.Check(
            demoOptions.Value.AccessKey,
            Request.Headers["X-Demo-Key"].FirstOrDefault(),
            environment.IsDevelopment());

        return access switch
        {
            DemoAccess.Allowed => null,
            DemoAccess.Denied => LogDenied(),
            _ => NotFound(),
        };

        IActionResult LogDenied()
        {
            logger.LogWarning("Demo: requisição com X-Demo-Key errada (IP {Ip}).", ClientId());
            return StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>Chave de rajada: no X-Forwarded-For do ingress (Container Apps) a lista
    /// é APPENDADA — os valores frontais o cliente pode forjar; o ÚLTIMO segmento é o
    /// que o proxy confiável viu (review R20: primeiro segmento forjava chaves de cota
    /// à vontade e derrubava a cota do vizinho).</summary>
    private string ClientId()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var segmentos = forwarded.Split(',');
            for (var i = segmentos.Length - 1; i >= 0; i--)
            {
                var valor = segmentos[i].Trim();
                if (!string.IsNullOrWhiteSpace(valor))
                {
                    return valor;
                }
            }
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static List<object> Map(IReadOnlyList<Outbound> items)
    {
        var replies = new List<object>();
        foreach (var item in items)
        {
            switch (item)
            {
                case Outbound.Text text:
                    replies.Add(new DemoTextReply("text", text.Body));
                    break;
                case Outbound.MediaFile file:
                    replies.Add(new DemoFileReply("file", file.FileName, file.ContentType,
                        file.Caption ?? string.Empty, Convert.ToBase64String(file.Content)));
                    break;
            }
        }

        return replies;
    }
}
