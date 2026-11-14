using AiCoachBff.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AiCoachBff.Tests;

/// <summary>
/// Orquestração do webhook (R20 F2): usuário conhecido → coach; desconhecido → lead +
/// onboarding (IA NUNCA); mídia → documento nativo; não-texto e falha → resposta fixa.
/// HandleAsync determinístico (o Enqueue só embrulha em Task).
/// </summary>
public sealed class WebhookServiceTests
{
    private static WebhookService Build(
        Mock<IAiCoachClient>? aiCoach = null,
        Mock<IMetaClient>? meta = null,
        BffOptions? options = null)
    {
        var ai = aiCoach ?? new Mock<IAiCoachClient>();
        var mt = meta ?? new Mock<IMetaClient>();
        return new WebhookService(new DedupeCache(100), ai.Object, mt.Object, options ?? Default(), NullLogger());
    }

    private static BffOptions Default() => new()
    {
        OnboardingMessage = "onboarding",
        NonTextMessage = "não-entendi",
        FallbackErrorMessage = "fallback",
    };

    private static ILogger<WebhookService> NullLogger() =>
        Mock.Of<ILogger<WebhookService>>();

    private static InboundMessage TextMessage(string body = "oi", string id = "wamid.1", string from = "5541999998888") =>
        new(From: from, Id: id, Type: "text", Text: new InboundText(body));

    [Fact]
    public async Task UsuarioConhecido_EsperaCoach_EmDevolveTexto()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>()))
            .ReturnsAsync(123462);
        ai.Setup(c => c.SendMessageAsync(123462, "oi", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Bom te ver! Qual foi o treino hoje?");

        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta);

        await service.HandleAsync(TextMessage(), profileName: null);

        meta.Verify(m => m.SendTextAsync("5541999998888", "Bom te ver! Qual foi o treino hoje?", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UsuarioDesconhecido_CriaLead_EmMandaOnboarding_NuncaChamaIA()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>()))
            .ReturnsAsync((int?)null);
        ai.Setup(c => c.CreateLeadAsync("5541999998888", "Eduardo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);

        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta);

        await service.HandleAsync(TextMessage(), profileName: "Eduardo");

        ai.Verify(c => c.CreateLeadAsync("5541999998888", "Eduardo", It.IsAny<CancellationToken>()), Times.Once);
        ai.Verify(c => c.SendMessageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        meta.Verify(m => m.SendTextAsync("5541999998888", "onboarding", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CoachDevolvePdf_BaixaEEntregaDocumento_CaptionSemOLink()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync(123462);
        ai.Setup(c => c.SendMessageAsync(123462, "manda o pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync("PDF do cronograma pronto: /chat/media/media-f836 (válido por 24h).");
        ai.Setup(c => c.DownloadMediaAsync("media-f836", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaDownload(new byte[] { 1, 2, 3 }, "application/pdf", "Cronograma.pdf"));

        var meta = new Mock<IMetaClient>();
        meta.Setup(m => m.UploadMediaAsync(It.IsAny<byte[]>(), "application/pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync("graph-123");
        var service = Build(aiCoach: ai, meta: meta);

        await service.HandleAsync(TextMessage("manda o pdf"), profileName: null);

        meta.Verify(m => m.UploadMediaAsync(It.IsAny<byte[]>(), "application/pdf", It.IsAny<CancellationToken>()), Times.Once);
        meta.Verify(m => m.SendDocumentAsync("5541999998888", "graph-123", "Cronograma.pdf", "PDF do cronograma pronto: (válido por 24h).", It.IsAny<CancellationToken>()), Times.Once);
        meta.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CoachDevolveFoto_EntregaImagemNativa()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync(123462);
        ai.Setup(c => c.SendMessageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Foto registrada: /chat/media/media-foto-0001");
        ai.Setup(c => c.DownloadMediaAsync("media-foto-0001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaDownload(new byte[] { 9 }, "image/jpeg", "foto.jpg"));

        var meta = new Mock<IMetaClient>();
        meta.Setup(m => m.UploadMediaAsync(It.IsAny<byte[]>(), "image/jpeg", It.IsAny<CancellationToken>()))
            .ReturnsAsync("graph-img");
        var service = Build(aiCoach: ai, meta: meta);

        await service.HandleAsync(TextMessage(), profileName: null);

        meta.Verify(m => m.SendImageAsync("5541999998888", "graph-img", "Foto registrada:", It.IsAny<CancellationToken>()), Times.Once);
        meta.Verify(m => m.SendDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CaptionSaiDoTexto_SemOLink()
    {
        var options = Default();
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync(123462);
        ai.Setup(c => c.SendMessageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Segue: /chat/media/media-f836");
        ai.Setup(c => c.DownloadMediaAsync("media-f836", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaDownload(new byte[] { 1 }, "application/pdf", null));

        var meta = new Mock<IMetaClient>();
        meta.Setup(m => m.UploadMediaAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("graph-x");
        var service = Build(aiCoach: ai, meta: meta, options: options);

        await service.HandleAsync(TextMessage(), profileName: null);

        meta.Verify(m => m.SendDocumentAsync("5541999998888", "graph-x", It.IsAny<string>(), "Segue:", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MensagemNaoTexto_RespostaFixaSemIA()
    {
        var ai = new Mock<IAiCoachClient>();
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta);

        var audio = new InboundMessage(From: "5541999998888", Id: "wamid.2", Type: "audio", Text: null!);
        await service.HandleAsync(audio, profileName: null);

        meta.Verify(m => m.SendTextAsync("5541999998888", "não-entendi", It.IsAny<CancellationToken>()), Times.Once);
        ai.Verify(c => c.SendMessageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReentregaDedupe_ignora()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        ai.Setup(c => c.SendMessageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ok");
        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta);

        await service.HandleAsync(TextMessage(), profileName: null);
        await service.HandleAsync(TextMessage(), profileName: null); // mesmo id

        meta.Verify(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CoachFalha_RespondeFallback_SemPropagar()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>())).ReturnsAsync(123462);
        ai.Setup(c => c.SendMessageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("503"));

        var meta = new Mock<IMetaClient>();
        var service = Build(aiCoach: ai, meta: meta);

        var act = () => service.HandleAsync(TextMessage(), profileName: null);

        await act.Should().NotThrowAsync();
        meta.Verify(m => m.SendTextAsync("5541999998888", "fallback", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Enqueue_NaoPropagaExcecao()
    {
        var ai = new Mock<IAiCoachClient>();
        ai.Setup(c => c.LookupUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("explodiu"));
        var meta = new Mock<IMetaClient>();
        meta.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = Build(aiCoach: ai, meta: meta);

        var act = () => service.Enqueue(TextMessage(), null);

        act.Should().NotThrow();
        await Task.Delay(300); // dá chance do background terminar (log do catch)
        meta.Verify(m => m.SendTextAsync("5541999998888", "fallback", It.IsAny<CancellationToken>()), Times.Once);
    }
}
