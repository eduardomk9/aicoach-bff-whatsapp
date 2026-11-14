using System.Text;
using AiCoachBff.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AiCoachBff.Tests;

/// <summary>
/// Webhook (R20 F2): handshake GET devolve o hub.challenge TEXTO PURO; token errado
/// → 403; POST ack 200 e processa (clients do serviço mocked na DI — nenhum HTTP real
/// sai no teste); payload inválido ack de qualquer forma.
/// </summary>
public sealed class WebhookControllerTests(WebApplicationFactory<global::Program> factory)
    : IClassFixture<WebApplicationFactory<global::Program>>
{
    private static readonly string Payload =
        """
        {
          "object": "whatsapp_business_account",
          "entry": [{
            "id": "WABA-1",
            "changes": [{
              "field": "messages",
              "value": {
                "messaging_product": "whatsapp",
                "contacts": [{ "profile": { "name": "Eduardo" }, "wa_id": "5541999998888" }],
                "messages": [{ "from": "5541999998888", "id": "wamid.test-1", "timestamp": "1731500000", "type": "text", "text": { "body": "oi coach" } }]
              }
            }]
          }]
        }
        """;

    private HttpClient CreateClient()
    {
        var aiCoach = new Mock<IAiCoachClient>();
        aiCoach.Setup(c => c.LookupUserAsync("5541999998888", It.IsAny<CancellationToken>()))
            .ReturnsAsync((int?)null);
        aiCoach.Setup(c => c.CreateLeadAsync("5541999998888", "Eduardo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        var meta = new Mock<IMetaClient>();
        meta.Setup(m => m.SendTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Meta:WebhookVerifyToken", "verify-token-do-teste");
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IAiCoachClient>(_ => aiCoach.Object);
                services.AddScoped<IMetaClient>(_ => meta.Object);
            });
        }).CreateClient();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetVerify_TokenCerto_DevolveChallengeTextoPuro(bool certo)
    {
        var client = CreateClient();
        var url = $"/webhook?hub.mode=subscribe&hub.verify_token={(certo ? "verify-token-do-teste" : "errado")}&hub.challenge=ECHO-DOSTESTES";

        var response = await client.GetAsync(url);

        if (certo)
        {
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Be("ECHO-DOSTESTES");
        }
        else
        {
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task PostWebhook_PayloadValido_Ack200EProcessa()
    {
        var client = CreateClient();
        var content = new StringContent(Payload, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/webhook", content);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostWebhook_PayloadNaoJson_AckDeQualquerForma()
    {
        var client = CreateClient();
        var content = new StringContent("nao-e-json", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/webhook", content);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Healthz_DevolveSaudavel()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("healthy");
    }
}
