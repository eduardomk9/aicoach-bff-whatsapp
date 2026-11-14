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
}
