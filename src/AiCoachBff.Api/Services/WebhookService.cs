using System.Text.RegularExpressions;

namespace AiCoachBff.Api.Services;

/// <summary>
/// Orquestração da mensagem do WhatsApp (R20 F2): fila por usuário (UserGate) → dedupe →
/// lookup telefone → usuário conhecido espera o coach (send-message bloqueante) e responde;
/// telefone novo cria o LEAD pendente de pagamento e responde onboarding (a IA NUNCA é
/// chamada); texto com link(s) de mídia vira documento/imagem nativa (um anexo por link,
/// caption no 1º, degradando para texto em falha); falha da API vira mensagem amigável.
/// Processa EM BACKGROUND (o ack da Meta volta &lt;10s — a espera do LLM não segura HTTP).
///
/// A esteira NÃO conhece a Meta: todo item de saída é um Outbound entregue num
/// IOutboundSink. O HandleAsync (webhook) usa o MetaSink — Graph idêntico ao de antes do
/// bocal (upload → documento/imagem, mesmas assinaturas); o SimulateAsync (canal demo,
/// R20 — front provisório mobile-first) coleciona os itens e a página mostra. Mesma
/// esteira, dois meios de entrega.
/// </summary>
public sealed class WebhookService(
    DedupeCache dedupe,
    UserGate userGate,
    IAiCoachClient aiCoach,
    IMetaClient meta,
    BffOptions bff,
    DemoOptions demo,
    ILogger<WebhookService> logger)
{
    /// <summary>Enfileira o processamento (fire-and-forget; exceção logada, nunca propagada).
    /// O ack da Meta volta &lt;10s — a espera do LLM (~3min) nunca segura o HTTP.
    /// O fluxo entra na FILA POR USUÁRIO (DeliveryAsync) antes do processamento.</summary>
    public void Enqueue(InboundMessage message, string? profileName)
    {
        _ = Task.Run(() => DeliveryAsync(message, profileName));
    }

    /// <summary>
    /// Serializa o fluxo por waId (UserGate): rajada do WhatsApp — "treino de hj" seguido
    /// de "e a dieta?" segundos depois — bate dentro da espera do coach, e dois
    /// send-message do MESMO userId em paralelo corrompem a sessão (insert duplicado,
    /// corpus fora de ordem, estado dos fluxos misturado). Um turno de cada vez, na ordem.
    /// </summary>
    private async Task DeliveryAsync(InboundMessage message, string? profileName)
    {
        var gate = userGate.GetGate(PhoneNormalizer.Canonical(message.From));
        await gate.WaitAsync();
        try
        {
            await HandleAsync(message, profileName);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Processa a mensagem pelo canal WhatsApp (o Enqueue chama em Task; o teste
    /// chama direto). O sink fala com o Graph exatamente como antes do bocal.</summary>
    public async Task HandleAsync(InboundMessage message, string? profileName)
    {
        // Dedupe na PORTA (a entrega da Meta re-envia o mesmo id): o HandleCoreAsync é a
        // ESTEIRA — não conhece ids, só conteúdo. Um id visto aqui morre aqui.
        if (!dedupe.TryAcquire(message.Id))
        {
            logger.LogWarning("Mensagem {MessageId} repetida (reentrega da Meta) — ignorada.", message.Id);
            return;
        }

        await HandleCoreAsync(message, profileName, new MetaSink(meta), CancellationToken.None);
    }

    /// <summary>Canal demo (R20): roda a MESMA esteira sincronamente, coleta o que seria
    /// entregue no WhatsApp e devolve pra página. Fila por usuário IGUAL ao webhook (a
    /// rajada do chat web não corrompe a sessão). Número JÁ CADASTRADO fora da allowlist
    /// da demo é recusado ANTES da esteira — sequestro de sessão (review R20): com a
    /// chave em mãos, alguém digitava o número real de um cliente e o turno rodava NA
    /// SESSÃO dele (corpus poluído, PDF pessoal na resposta). SEM dedupe na porta demo:
    /// o id nasce por requisição (demo-{Guid}) — não existe reentrega real — e o cache
    /// do webhook não pode ser gasto com id sintético (a rajada do demo evictaria ids
    /// REAIS da Meta no LRU compartilhado, review R20). Exceção inesperada NÃO escapa
    /// (a esteira transforma em fallback de texto) — só o cancelamento da requisição
    /// (visitante fechou a página) pode interromper aqui.</summary>
    public async Task<IReadOnlyList<Outbound>> SimulateAsync(InboundMessage message, string? profileName, CancellationToken cancellationToken)
    {
        var waId = PhoneNormalizer.Canonical(message.From);

        if (await BloqueadoSemAcessoAsync(waId, cancellationToken))
        {
            return [new Outbound.Text(bff.KnownUserDemoMessage)];
        }

        var gate = userGate.GetGate(waId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var itens = new List<Outbound>();
            await HandleCoreAsync(message, profileName, new CollectorSink(itens), cancellationToken);
            return itens;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Veredito de bloqueio da demo (R20 admin): conhecido SEM IsAllowed =
    /// bloqueado (o cliente real pertence ao WhatsApp oficial); permitido conversa.
    /// O flag mora no BANCO (Users.IsAllowed, ddl-004) — a lista de env morreu.
    /// Lookup falhou (API indisponível) = deixa passar — o HandleCoreAsync transforma
    /// a falha na mensagem de fallback; e uma falha de lookup NUNCA cria lead
    /// (a exceção impede o CreateLeadAsync), então não há falso onboarding.</summary>
    private async Task<bool> BloqueadoSemAcessoAsync(string waId, CancellationToken cancellationToken)
    {
        try
        {
            var lookup = await aiCoach.LookupUserAsync(waId, cancellationToken);
            if (!lookup.Known)
            {
                return false;
            }

            return !lookup.Allowed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Demo: lookup de bloqueio falhou para {WaId} — seguindo o fluxo normal.", waId);
            return false;
        }
    }

    /// <summary>Processa a mensagem (o webhook usa MetaSink; a demo usa CollectorSink).
    /// DEDUPE FICA NA PORTA (HandleAsync/SimulateAsync) — nunca aqui: o id que a porta
    /// registrou faria esteira descartar o turno como "reentrega" (bug do duplo dedupe).</summary>
    private async Task HandleCoreAsync(InboundMessage message, string? profileName, IOutboundSink sink, CancellationToken cancellationToken)
    {
        var waId = PhoneNormalizer.Canonical(message.From);
        try
        {
            if (!string.Equals(message.Type, "text", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(message.Text?.Body))
            {
                logger.LogInformation("Mensagem {MessageId} sem texto (type={Type}).", message.Id, message.Type ?? "?");
                await sink.SendAsync(waId, new Outbound.Text(bff.NonTextMessage), cancellationToken);
                return;
            }

            await ReplyFromCoachAsync(waId, message.Text.Body, profileName, sink, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Espera do coach estourou para {WaId} — resposta de fallback.", waId);
            await SendSilentlyAsync(waId, bff.FallbackErrorMessage, sink);
        }
        catch (Exception exception)
        {
            // O usuário já não recebeu nada: resposta amigável + log completo.
            logger.LogError(exception, "Falha ao processar mensagem {MessageId} de {WaId}.", message.Id, waId);
            await SendSilentlyAsync(waId, bff.FallbackErrorMessage, sink);
        }
    }

    private async Task ReplyFromCoachAsync(string waId, string text, string? profileName, IOutboundSink sink, CancellationToken cancellationToken)
    {
        var lookup = await aiCoach.LookupUserAsync(waId, cancellationToken);
        if (!lookup.Known)
        {
            // Gate de leads (R20 F3): cadastro provisório pendente de pagamento;
            // onboarding sem IA — o coach não conversa com quem não é usuário.
            var leadId = await aiCoach.CreateLeadAsync(waId, profileName, cancellationToken);
            logger.LogInformation("Lead {LeadId} criado para {WaId} (perfil {Profile}).", leadId, waId, profileName ?? "(sem nome)");
            await sink.SendAsync(waId, new Outbound.Text(bff.OnboardingMessage), cancellationToken);
            return;
        }

        var reply = await aiCoach.SendMessageAsync(lookup.UserId!.Value, text, cancellationToken);

        // Mídia nativa: o coach entrega PDF/foto com o link RELATIVO /chat/media/{id} —
        // baixa da API e entrega documento no WhatsApp (docs\whatsapp-pdf.md da API).
        // Multi-intenção (G19 da API): 2+ links no MESMO AgentMessage vira 2+ anexos —
        // entregar só o 1º deixaria o resto como texto relativo morto na conversa.
        var links = MediaLinkExtractor.FindAll(reply);
        if (links.Count > 0)
        {
            await DeliverMediaAsync(waId, reply, links, sink, cancellationToken);
            return;
        }

        await sink.SendAsync(waId, new Outbound.Text(reply), cancellationToken);
    }

    private async Task DeliverMediaAsync(string waId, string reply, IReadOnlyList<string> links, IOutboundSink sink, CancellationToken cancellationToken)
    {
        // Caption = a resposta INTEIRA do coach sem TODOS os links — só no PRIMEIRO anexo
        // (os demais entregam o artefato a seco). Espaços corridos colapsam; a estrutura
        // de parágrafos (\n\n) do relatório é preservada. O CapCaption do MetaClient
        // impõe o teto de 1024 do Graph na ponta.
        var captionBase = reply;
        foreach (var link in links)
        {
            captionBase = captionBase.Replace(link, " ", StringComparison.OrdinalIgnoreCase);
        }

        captionBase = Regex.Replace(captionBase, @"[ \t]+", " ");
        captionBase = Regex.Replace(captionBase, @"(\r?\n){3,}", "\n\n").Trim();
        // Teto do Graph (1024) imposto AQUI (a caption é do fluxo) e de novo no MetaClient
        // (última linha de defesa para qualquer chamador).
        var caption = string.IsNullOrWhiteSpace(captionBase) ? "Aqui está o seu arquivo." : MetaClient.CapCaption(captionBase)!;

        try
        {
            for (var i = 0; i < links.Count; i++)
            {
                var mediaId = MediaLinkExtractor.MediaIdOf(links[i]) ?? throw new InvalidOperationException($"Link de mídia sem id: {links[i]}");
                var media = await aiCoach.DownloadMediaAsync(mediaId, cancellationToken);
                var captionDoAnexo = i == 0 ? caption : null;
                var fileName = media.FileName ?? "aicoach.pdf";
                // No sink do WhatsApp: upload no Graph + documento/imagem (aqui dentro —
                // Graph falhando cai no catch abaixo e degrada, igual antes do bocal).
                await sink.SendAsync(waId, new Outbound.MediaFile(captionDoAnexo, fileName, media.ContentType, media.Content, mediaId), cancellationToken);
            }
        }
        catch (Exception exception)
        {
            // Mídia falhou (id expirado/replica da API sem o arquivo, Graph fora, caption
            // rejeitada): o coach JÁ respondeu e o turno já avançou — o fallback geral
            // ("problema pra pensar") mentiria e apagaria a resposta paga. Degrada para o
            // TEXTO do coach sem os links (o link relativo não resolve no WhatsApp); se
            // não sobrou texto nenhum, frase honesta de arquivo.
            logger.LogError(exception, "Entrega de mídia falhou para {WaId} — degradando para o texto do coach.", waId);
            var texto = string.IsNullOrWhiteSpace(captionBase)
                ? "O arquivo não pôde ser entregue agora — me pede de novo que eu refaço."
                : captionBase;
            await SendSilentlyAsync(waId, texto, sink);
        }
    }

    private async Task SendSilentlyAsync(string waId, string body, IOutboundSink sink)
    {
        try
        {
            await sink.SendAsync(waId, new Outbound.Text(body), CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Nem a resposta de fallback saiu (Graph indisponível): não há mais o que fazer — log.
            logger.LogError(exception, "Fallback também falhou para {WaId}.", waId);
        }
    }

    /// <summary>Sink do WhatsApp: o mesmo contrato de Graph de antes do bocal — texto direto;
    /// mídia com upload ANTES do envio (ordem e assinaturas idênticas às dos testes).</summary>
    private sealed class MetaSink(IMetaClient meta) : IOutboundSink
    {
        public async Task SendAsync(string waId, Outbound item, CancellationToken cancellationToken)
        {
            switch (item)
            {
                case Outbound.Text text:
                    await meta.SendTextAsync(waId, text.Body, cancellationToken);
                    return;
                case Outbound.MediaFile file:
                    var graphId = await meta.UploadMediaAsync(file.Content, file.ContentType, cancellationToken);
                    if (file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        await meta.SendImageAsync(waId, graphId, file.Caption, cancellationToken);
                        return;
                    }

                    await meta.SendDocumentAsync(waId, graphId, file.FileName, file.Caption, cancellationToken);
                    return;
            }
        }
    }

    /// <summary>Sink da demo: só anota a ordem exata do que seria entregue.</summary>
    private sealed class CollectorSink(List<Outbound> itens) : IOutboundSink
    {
        public Task SendAsync(string waId, Outbound item, CancellationToken cancellationToken)
        {
            itens.Add(item);
            return Task.CompletedTask;
        }
    }
}
