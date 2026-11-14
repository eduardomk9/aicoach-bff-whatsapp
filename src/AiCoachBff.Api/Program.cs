using AiCoachBff.Api.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Config por secção (defaults não sensíveis no appsettings.json; segredos por
// env / secretref nas Container Apps — Chave__Aninhada=secretref:<secret>).
builder.Services.Configure<MetaOptions>(builder.Configuration.GetSection(MetaOptions.Section));
builder.Services.Configure<AiCoachOptions>(builder.Configuration.GetSection(AiCoachOptions.Section));
builder.Services.Configure<BffOptions>(builder.Configuration.GetSection(BffOptions.Section));

// As options cruas para os construtores dos clients (o DI dos typed clients
// não injeta IOptions — injeta a classe).
builder.Services.AddSingleton<MetaOptions>(sp => sp.GetRequiredService<IOptions<MetaOptions>>().Value);
builder.Services.AddSingleton<AiCoachOptions>(sp => sp.GetRequiredService<IOptions<AiCoachOptions>>().Value);
builder.Services.AddSingleton<BffOptions>(sp => sp.GetRequiredService<IOptions<BffOptions>>().Value);

builder.Services.AddControllers();

// HttpClient do Graph: timeout folgado para upload de mídia.
builder.Services.AddHttpClient<IMetaClient, MetaClient>("meta", (_, client) =>
{
    client.BaseAddress = new Uri("https://graph.facebook.com");
    client.Timeout = TimeSpan.FromSeconds(120);
});

// HttpClients da API: o de chat espera o LLM (bloqueante, pior caso ~3min) —
// timeout = MaxWaitSeconds + folga; o curto serve login/lookup/lead. O
// AiCoachClient fala com eles pelo nome (IHttpClientFactory no próprio client).
var maxWait = builder.Configuration.GetSection(BffOptions.Section).Get<BffOptions>()?.MaxWaitSeconds ?? 180;
builder.Services.AddHttpClient("aicoach", (_, client) =>
{
    client.BaseAddress = new Uri(builder.Configuration["AiCoach:BaseUrl"] ?? "http://localhost:5105");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient("aicoach-chat", (_, client) =>
{
    client.BaseAddress = new Uri(builder.Configuration["AiCoach:BaseUrl"] ?? "http://localhost:5105");
    client.Timeout = TimeSpan.FromSeconds(maxWait + 60);
});

builder.Services.AddSingleton(sp =>
{
    var options = builder.Configuration.GetSection(BffOptions.Section).Get<BffOptions>() ?? new BffOptions();
    return new DedupeCache(options.DedupeCacheSize);
});
builder.Services.AddScoped<IAiCoachClient, AiCoachClient>();
builder.Services.AddScoped<WebhookService>();

var app = builder.Build();

// Probe de saúde (Container Apps + verificação de deploy): anônimo, sem dependências.
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

app.MapControllers();

app.Run();

public partial class Program;
