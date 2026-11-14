using AiCoachBff.Api.Services;
using FluentAssertions;
using Xunit;

namespace AiCoachBff.Tests;

/// <summary>
/// Travas do canal demo (R20): chave de acesso (o endpoint NEM EXISTE em production
/// sem chave — fail-closed) e janela fixa de rajada por IP.
/// </summary>
public sealed class DemoGateTests
{
    [Theory]
    [InlineData("segredo", "segredo", true, DemoAccess.Allowed)]
    [InlineData("segredo", "errada", true, DemoAccess.Denied)]
    [InlineData("segredo", null, true, DemoAccess.Denied)]
    [InlineData(null, null, true, DemoAccess.Allowed)]   // dev sem chave: aberto
    [InlineData(null, null, false, DemoAccess.Disabled)] // produção sem chave: NEM EXISTE
    [InlineData(null, "qualquer", false, DemoAccess.Disabled)]
    [InlineData("segredo", "segredo", false, DemoAccess.Allowed)]
    public void Verifica_Acesso(string? configurada, string? fornecida, bool dev, DemoAccess esperado)
    {
        DemoAuthorization.Check(configurada, fornecida, dev).Should().Be(esperado);
    }

    [Fact]
    public void Limiter_ExigeLimite_ELiberaNovaJanela()
    {
        var limiter = new DemoLimiter();
        var agora = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var janela = TimeSpan.FromMinutes(15);

        for (var i = 1; i <= 30; i++)
        {
            limiter.Allow("ip-1", agora, 30, janela).Should().BeTrue($"a {i}ª mensagem é cota");
        }

        limiter.Allow("ip-1", agora, 30, janela).Should().BeFalse("31ª estourou a janela");

        // Outra janela (15min depois): cota nova.
        limiter.Allow("ip-1", agora.AddMinutes(15), 30, janela).Should().BeTrue();

        // IP diferente: cota própria.
        limiter.Allow("ip-2", agora, 30, janela).Should().BeTrue();
    }

    [Fact]
    public void Limiter_JanelasDiferentes_ContamSeparado()
    {
        var limiter = new DemoLimiter();
        var janela = TimeSpan.FromMinutes(15);
        var t0 = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

        // 2 bucket boundaries: dentro da 1ª janela, e logo DEPOIS da troca.
        var fim = t0.Add(janela).AddSeconds(1);
        limiter.Allow("ip-3", t0, 1, janela).Should().BeTrue();
        limiter.Allow("ip-3", t0.AddSeconds(1), 1, janela).Should().BeFalse();
        limiter.Allow("ip-3", fim, 1, janela).Should().BeTrue("janela nova recomeça a cota");
    }

    [Fact]
    public void Limiter_TetoDeEntradas_ZeraJanelasEmVezDeCrescerSemLimite()
    {
        // Review R20: atacante forjando chaves não pode inflar o dicionário sem teto.
        // Estourou o teto → Clear() de vez (todo mundo recomeça a própria janela,
        // inclusive o atacante) — a chave antiga que já tinha gasto a cota VOLTA.
        var limiter = new DemoLimiter();
        var agora = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var janela = TimeSpan.FromMinutes(15);

        for (var i = 0; i < 10_001; i++)
        {
            limiter.Allow($"chave:{i}", agora, 1, janela);
        }

        limiter.Allow("chave:0", agora, 1, janela).Should().BeTrue(
            "o teto de entradas zera as janelas (O(1)) em vez de crescer sem limite");
    }
}
