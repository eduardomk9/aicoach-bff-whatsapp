using AiCoachBff.Api.Services;
using FluentAssertions;
using Xunit;

namespace AiCoachBff.Tests;

public sealed class MediaLinkExtractorTests
{
    [Fact]
    public void Find_HaLinkAchaOId()
    {
        var link = MediaLinkExtractor.Find("PDF do cronograma pronto: /chat/media/media-f836166db65346799b009217c49241c3 (válido por 24h).");

        link.Should().Be("/chat/media/media-f836166db65346799b009217c49241c3");
        MediaLinkExtractor.MediaIdOf(link!).Should().Be("media-f836166db65346799b009217c49241c3");
    }

    [Fact]
    public void Find_SemLink_DevolveNulo()
    {
        MediaLinkExtractor.Find("Bom treino hoje! 💪").Should().BeNull();
        MediaLinkExtractor.Find(null).Should().BeNull();
        MediaLinkExtractor.Find(" ").Should().BeNull();
    }

    [Fact]
    public void Find_LinkMaiusculo_CaseInsensitive()
    {
        MediaLinkExtractor.Find("Segue: /CHAT/MEDIA/media-ABC12345").Should().Be("/CHAT/MEDIA/media-ABC12345");
    }

    [Fact]
    public void MediaIdOf_LinkSemId_DevolveNulo()
    {
        MediaLinkExtractor.MediaIdOf("/chat/media/").Should().BeNull();
        MediaLinkExtractor.MediaIdOf(null).Should().BeNull();
    }

    [Fact]
    public void FindAll_MultiPedido_TresLinksDistintosNaOrdem()
    {
        var texto = "PDF pronto: /chat/media/media-aaa (24h).\n\nRelatório: /chat/media/media-bbb (24h).\n\nFoto: /chat/media/media-ccc (24h).";

        var links = MediaLinkExtractor.FindAll(texto);

        links.Should().Equal("/chat/media/media-aaa", "/chat/media/media-bbb", "/chat/media/media-ccc");
    }

    [Fact]
    public void FindAll_LinkRepetido_VemUmaVez()
    {
        var links = MediaLinkExtractor.FindAll("segue /chat/media/media-aaa e de novo /chat/media/media-aaa");

        links.Should().Equal("/chat/media/media-aaa");
    }

    [Fact]
    public void FindAll_SemLink_DevolveListaVazia()
    {
        MediaLinkExtractor.FindAll("Bom treino hoje!").Should().BeEmpty();
        MediaLinkExtractor.FindAll(null).Should().BeEmpty();
    }
}
