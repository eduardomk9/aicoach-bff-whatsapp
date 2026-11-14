using AiCoachBff.Api.Services;
using FluentAssertions;
using Xunit;

namespace AiCoachBff.Tests;

public sealed class PhoneNormalizerTests
{
    [Fact]
    public void CanonicalWaId_MantemApenasDigitos()
    {
        PhoneNormalizer.Canonical("5541999998888").Should().Be("5541999998888");
        PhoneNormalizer.Canonical("+55 41 99999-8888").Should().Be("5541999998888");
        PhoneNormalizer.Canonical("").Should().Be("");
    }
}
