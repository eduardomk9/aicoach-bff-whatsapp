using AiCoachBff.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AiCoachBff.Tests;

/// <summary>
/// Canal demo (R20): a MESMA esteira do webhook, sincrona, com saída COLETADA em vez de
/// entregue via Graph. A Meta NUNCA é chamada aqui — e a ordem dos itens é a que o
/// WhatsApp receberia. Bloqueio de cliente (review R20): número conhecido fora da
/// allowlist da demo é recusado ANTES da esteira — a sessão real não pode ser tocada.
/// </summary>
public sealed class WebhookServiceSimulateTests
{
    /// <summary>Allowlist de testes: o número dos fluxos "conhecido" está nela (o dono).
    /// Sem allowlist, o default é NENHUM cliente pela demo — os fluxos abaixo testam o
    /// caminho do coach, não o bloqueio.</summary>
    private static DemoOptions Dono() => new() { AllowedPhones = ["5541999998888"] };

    private static WebhookService Build(
        Mock<IAiCoachClient>? aiCoach = null,
        Mock<IMetaClient>? meta = null,
        BffOptions? options = null,
        DemoOptions? demo = null,
        int dedupeCap = 100)
    {
        var ai = aiCoach ?? new Mock<IAiCoachClient>();
        var mt = meta ?? new Mock<IMetaClient>();
        return new WebhookService(new DedupeCache(dedupeCap), new UserGate(), ai.Object, mt.Object,
            options ?? Default(), demo ?? new DemoOptions(), Mock.Of<ILogger<WebhookService>>());
    }

    private static BffOptions Default() => new()
    {
        OnboardingMessage = "onboarding",
        NonTextMessage = "não-entendi",
        FallbackErrorMessage = "fallback",
        KnownUserDemoMessage = "cliente",
    };

    private static InboundMessage Texto(string body = "oi", string id = "demo.a1", string from = "5541999998888") =>
        new(From: from, Id: id, Type: "text", Text: new InboundText(body));

    [Fact]
    public async Task Simulate_UsuarioConhecido_ColetaTextoDoCoach_NuncaChamaMeta()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync(123462);
        ai.Setup(c => c.SendMessageAsync(123462, "oi", It.IsAny<CancellationToken>())).ReturnsAsync("Bom te ver! Qual foi o treino hoje?");
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta, demo: Dono());

        var replies = await service.SimulateAsync(Texto(), null, CancellationToken.None);

        replies.Should().ContainSingle().Which.Should().BeOfType<Outbound.Text>()
            .Which.Body.Should().Be("Bom te ver! Qual foi o treino hoje?");
        meta.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Simulate_ConhecidoForaDaAllowlist_RecusaSemTocarSessao()
    {
        // Sequestro de sessão (review R20): com a chave da demo em mãos, alguém digitava
        // o número REAL de um cliente → lookup devolvia o userId e o turno rodava NA
        // sessão dele (corpus poluído, PDF pessoal na resposta). Default seguro: allowlist
        // vazia = NENHUM cliente pela demo — coach e lead NEM são chamados.
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync(123462);
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta);

        var replies = await service.SimulateAsync(Texto(), null, CancellationToken.None);

        replies.Should().ContainSingle().Which.Should().BeOfType<Outbound.Text>()
            .Which.Body.Should().Be("cliente");
        ai.Verify(c => c.SendMessageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        ai.Verify(c => c.CreateLeadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        meta.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Simulate_LookupDoBloqueioFalha_DeixaPassar_EViraFallback()
    {
        // API fora no momento do bloqueio: NÃO dá pra decidir se o número é de cliente.
        // Deixa passar — o HandleCoreAsync transforma a falha em fallback, e uma falha
        // de lookup NUNCA cria lead (não há falso onboarding de cliente real).
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API caiu"));
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta);

        var replies = await service.SimulateAsync(Texto(), null, CancellationToken.None);

        replies.Should().ContainSingle().Which.Should().BeOfType<Outbound.Text>()
            .Which.Body.Should().Be("fallback");
        ai.Verify(c => c.CreateLeadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Simulate_Desconhecido_OnboardingComNome_NuncaChamaIA()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync((int?)null);
        ai.Setup(c => c.CreateLeadAsync("5541999998888", "Eduardo", It.IsAny<CancellationToken>())).ReturnsAsync(7);
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta);

        var replies = await service.SimulateAsync(Texto(), "Eduardo", CancellationToken.None);

        ai.Verify(c => c.CreateLeadAsync("5541999998888", "Eduardo", It.IsAny<CancellationToken>()), Times.Once);
        ai.Verify(c => c.SendMessageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        replies.Should().ContainSingle().Which.Should().BeOfType<Outbound.Text>()
            .Which.Body.Should().Be("onboarding");
    }

    [Fact]
    public async Task Simulate_ComPdf_ViraMediaFile_ComBytesENomeECaptionSemOLink()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync(123462);
        ai.Setup(c => c.SendMessageAsync(123462, "manda o pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync("PDF do cronograma pronto: /chat/media/media-f836 (válido por 24h).");
        ai.Setup(c => c.DownloadMediaAsync("media-f836", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaDownload(bytes, "application/pdf", "Cronograma.pdf"));
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta, demo: Dono());

        var replies = await service.SimulateAsync(Texto("manda o pdf"), null, CancellationToken.None);

        var arquivo = replies.Should().ContainSingle().Which.Should().BeOfType<Outbound.MediaFile>().Which;
        arquivo.FileName.Should().Be("Cronograma.pdf");
        arquivo.ContentType.Should().Be("application/pdf");
        arquivo.Content.Should().Equal(bytes);
        arquivo.MediaId.Should().Be("media-f836");
        arquivo.Caption.Should().Be("PDF do cronograma pronto: (válido por 24h).");
        meta.Verify(m => m.UploadMediaAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        meta.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Simulate_DoisObjectLinks_OrdemIgualAoWhatsApp_CaptionSoNoPrimeiro()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync(123462);
        ai.Setup(c => c.SendMessageAsync(123462, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("PDF do cronograma pronto: /chat/media/media-aaa (válido por 24h).\n\nRelatório de outubro pronto: /chat/media/media-bbb (válido por 24h).");
        ai.Setup(c => c.DownloadMediaAsync("media-aaa", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaDownload(new byte[] { 1 }, "application/pdf", "Cronograma.pdf"));
        ai.Setup(c => c.DownloadMediaAsync("media-bbb", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaDownload(new byte[] { 2 }, "application/pdf", "Relatorio.pdf"));
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta, demo: Dono());

        var replies = await service.SimulateAsync(Texto(), null, CancellationToken.None);

        var dois = replies.Should().HaveCount(2).And.Subject.OfType<Outbound.MediaFile>().ToList();
        dois[0].MediaId.Should().Be("media-aaa");
        dois[0].Caption.Should().Be("PDF do cronograma pronto: (válido por 24h).\n\nRelatório de outubro pronto: (válido por 24h).");
        dois[1].MediaId.Should().Be("media-bbb");
        dois[1].Caption.Should().BeNull();
    }

    [Fact]
    public async Task Simulate_MidiaQueFalha_DegradaParaTextoNoCollector()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync(123462);
        ai.Setup(c => c.SendMessageAsync(123462, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("PDF do cronograma pronto: /chat/media/media-expirou (válido por 24h).");
        ai.Setup(c => c.DownloadMediaAsync("media-expirou", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("404 expirado"));
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta, demo: Dono());

        var replies = await service.SimulateAsync(Texto(), null, CancellationToken.None);

        replies.Should().ContainSingle().Which.Should().BeOfType<Outbound.Text>()
            .Which.Body.Should().Be("PDF do cronograma pronto: (válido por 24h).");
        meta.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Simulate_MesmaEsteira_FalhaDoCoach_ViraFallbackColetado()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync(123462);
        ai.Setup(c => c.SendMessageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("503"));
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta, demo: Dono());

        var replies = await service.SimulateAsync(Texto(), null, CancellationToken.None);

        replies.Should().ContainSingle().Which.Should().BeOfType<Outbound.Text>()
            .Which.Body.Should().Be("fallback [503]");
    }

    [Fact]
    public async Task Simulate_SemDedupeDeDemo_MesmoIdReprocessa()
    {
        // O id da demo nasce POR REQUISIÇÃO (demo-{Guid} no controller) — não existe
        // reentrega real. A porta demo NÃO deduplica: segunda chamada processa de novo
        // (o dedupe que importa é o do webhook, e o cache dele não pode ser gasto com
        // id sintético — prova de evicção no teste seguinte).
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((int?)null);
        ai.Setup(c => c.CreateLeadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(7);
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta);

        var primeira = await service.SimulateAsync(Texto(), null, CancellationToken.None);
        var segunda = await service.SimulateAsync(Texto(), null, CancellationToken.None); // mesmo Id

        primeira.Should().NotBeEmpty();
        segunda.Should().NotBeEmpty("id da demo é sintético — nenhuma reentrega real pra deduplicar");
    }

    [Fact]
    public async Task Demo_RajadaDeIdsSinteticos_NaoEvictaDedupeDoWebhook()
    {
        // Cache TINHO (10) + rajada demo de 20: se a porta demo deduplicasse no MESMO
        // cache LRU do webhook (bug da review R20), o id REAL w.1 — o mais velho — seria
        // evictado e a reentrega da Meta geraria resposta DUPLA pro cliente.
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541888887777", It.IsAny<CancellationToken>())).ReturnsAsync(7);
        ai.Setup(c => c.SendMessageAsync(7, "oi", It.IsAny<CancellationToken>())).ReturnsAsync("coach responde");
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync((int?)null);
        ai.Setup(c => c.CreateLeadAsync("5541999998888", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(9);
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta, dedupeCap: 10);

        var w1 = new InboundMessage(From: "5541888887777", Id: "w.1", Type: "text", Text: new InboundText("oi"));
        await service.HandleAsync(w1, null);
        for (var i = 0; i < 20; i++)
        {
            await service.SimulateAsync(Texto(body: "msg " + i, id: "demo." + i), null, CancellationToken.None);
        }
        await service.HandleAsync(w1, null); // reentrega da Meta do MESMO id real

        meta.Verify(m => m.SendTextAsync("5541888887777", "coach responde", It.IsAny<CancellationToken>()), Times.Once,
            "a demo não gasta dedupe do webhook — o id real sobrevive à rajada sintética");
    }

    [Fact]
    public async Task Simulate_DoismoMesmoUsuario_Paralelo_AmbosRespondem_SemMisturar()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(10);
        ai.Setup(c => c.SendMessageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, string texto, CancellationToken _) =>
            {
                await Task.Delay(120);
                return $"resp:{texto}";
            });
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta, demo: Dono());

        var t1 = service.SimulateAsync(Texto("treino de hj", id: "demo.a"), null, CancellationToken.None);
        var t2 = service.SimulateAsync(Texto("e a dieta?", id: "demo.b"), null, CancellationToken.None);
        var esperados = await Task.WhenAll(t1, t2);
        var r1 = esperados[0];
        var r2 = esperados[1];

        var textos = r1.Concat(r2).OfType<Outbound.Text>().Select(t => t.Body).ToList();
        textos.Should().BeEquivalentTo(["resp:treino de hj", "resp:e a dieta?"],
            "a fila por Usuário serializa, e cada turno carrega o próprio coletor");
    }
}
