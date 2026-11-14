namespace AiCoachBff.Api.Services;

/// <summary>Item que o pipeline manda pro "fora" (a conversa). Quem decide o meio é o
/// sink: o canal WhatsApp entrega via Graph (upload → documento/imagem); o canal DEMO
/// (R20, front provisório) só COLETA — a mesma esteira, sem Meta na fio.</summary>
public abstract record Outbound
{
    /// <summary>Mensagem de texto (resposta do coach, onboarding, não-entendi, fallback).</summary>
    public sealed record Text(string Body) : Outbound;

    /// <summary>Anexo (1 por link do coach): bytes já baixados da API + caption pronta
    /// (só no 1º anexo do turno — null nos demais; teto 1024 imposto na origem). O sink
    /// do WhatsApp faz o upload no Graph; o sink do demo embute os bytes no JSON.</summary>
    public sealed record MediaFile(string? Caption, string FileName, string ContentType, byte[] Content, string MediaId) : Outbound;
}

/// <summary>Destino dos itens de saída. Toda saída da esteira passa por aqui — nenhum
/// método do pipeline toca na IMetaClient diretamente (o sink do WhatsApp que fala).</summary>
public interface IOutboundSink
{
    Task SendAsync(string waId, Outbound item, CancellationToken cancellationToken);
}
