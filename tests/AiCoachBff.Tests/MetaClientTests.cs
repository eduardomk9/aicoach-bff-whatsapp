using System.Text;
using AiCoachBff.Api.Services;
using FluentAssertions;
using Xunit;

namespace AiCoachBff.Tests;

/// <summary>
/// Guardrails do Graph no MetaClient (revisão R20): caption capped em 1024 (contrato
/// docs\whatsapp-pdf.md:58) sem rachar par surrogado, e SplitText sem emoji mojibake
/// na costura dos blocos de 4096.
/// </summary>
public sealed class MetaClientTests
{
    private static string Emoji() => "😎"; // "😎" — 1 par surrogado (2 chars)

    [Fact]
    public void CapCaption_TextoCurto_PassaIntacta()
    {
        var curta = new string('a', 500);

        MetaClient.CapCaption(curta).Should().Be(curta);
        MetaClient.CapCaption(null).Should().BeNull();
    }

    [Fact]
    public void CapCaption_Longa_TroncaA1024_ComReticencias()
    {
        var longa = new string('x', 1300);

        var cap = MetaClient.CapCaption(longa);

        cap.Should().NotBeNull();
        cap!.Length.Should().Be(MetaClient.MaxCaptionLength);
        cap.Should().EndWith("…");
    }

    [Fact]
    public void CapCaption_EmojiNaCostura_NaoRachaOPar()
    {
        // 1022 chars normais + emoji (par surrogado em 1022..1023) + sobra: o corte
        // cai na frente do par — tem que recuar 1 em vez de entregar high órfão.
        var longa = new string('a', 1022) + Emoji() + new string('b', 40);

        var cap = MetaClient.CapCaption(longa)!;

        cap.Length.Should().Be(1023); // 1022 chars + "…"
        cap[^1].Should().Be('…');
        char.IsHighSurrogate(cap[^2]).Should().BeFalse(); // nenhum high surrogado órfão colado no "…"
    }

    [Fact]
    public void SplitText_TextoCurto_UmBlocoIntacta()
    {
        MetaClient.SplitText("abc").Should().Equal("abc");
    }

    [Fact]
    public void SplitText_TextoLongo_FatiaNoLimite()
    {
        var body = new string('x', 4096 * 2 + 10);

        var blocos = MetaClient.SplitText(body).ToList();

        blocos.Should().HaveCount(3);
        blocos.Should().AllSatisfy(b => b.Length.Should().BeLessThanOrEqualTo(4096));
        string.Concat(blocos).Should().Be(body);
    }

    [Fact]
    public void SplitText_EmojiNaCostura_ParFicaInteiroNoBlocoSeguinte()
    {
        // Corte de 4096 cai em cima do emoji: size recua 1 e o par inteiro vai pro próximo.
        var body = new string('x', 4095) + Emoji() + "fim";

        var blocos = MetaClient.SplitText(body).ToList();

        blocos.Should().HaveCount(2);
        blocos[0].Length.Should().Be(4095);
        string.Concat(blocos).Should().Be(body);
        blocos[1].StartsWith(Emoji()).Should().BeTrue();
    }
}
