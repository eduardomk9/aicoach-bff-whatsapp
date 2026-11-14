using AiCoachBff.Api.Services;
using FluentAssertions;
using Xunit;

namespace AiCoachBff.Tests;

public sealed class DedupeCacheTests
{
    [Fact]
    public void TryAcquire_PrimeiraVezTrue_RepetidaFalse()
    {
        var cache = new DedupeCache(10);

        cache.TryAcquire("wamid.1").Should().BeTrue();
        cache.TryAcquire("wamid.1").Should().BeFalse();
        cache.TryAcquire("wamid.2").Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_EstouraCapacidade_EvictaMaisVelho()
    {
        var cache = new DedupeCache(2);

        cache.TryAcquire("a").Should().BeTrue();
        cache.TryAcquire("b").Should().BeTrue();
        cache.TryAcquire("c").Should().BeTrue(); // "a" sai (o mais velho)
        cache.TryAcquire("b").Should().BeFalse();
        cache.TryAcquire("a").Should().BeTrue(); // voltou: re-entrega antiga é processável de novo
    }
}
