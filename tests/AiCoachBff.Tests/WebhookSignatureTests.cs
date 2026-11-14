using System.Security.Cryptography;
using System.Text;
using AiCoachBff.Api.Services;
using FluentAssertions;
using Xunit;

namespace AiCoachBff.Tests;

/// <summary>Assinatura do webhook (X-Hub-Signature-256): hmac-sha256 + hex lowercase, prefixo sha256=.</summary>
public sealed class WebhookSignatureTests
{
    [Fact]
    public void Signature_FormatoECmparaComHmacEsperado()
    {
        var body = Encoding.UTF8.GetBytes("{\"object\":\"whatsapp_business_account\"}");

        var signature = MetaClient.Signature(body, "app-secret-do-teste");

        signature.Should().StartWith("sha256=").And.MatchRegex(@"^sha256=[0-9a-f]{64}$");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("app-secret-do-teste"));
        signature.Should().EndWith(Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant());
    }

    [Fact]
    public void SecretDiferente_SignatureDiferente()
    {
        var body = Encoding.UTF8.GetBytes("payload");

        MetaClient.Signature(body, "a").Should().NotBe(MetaClient.Signature(body, "b"));
    }
}
