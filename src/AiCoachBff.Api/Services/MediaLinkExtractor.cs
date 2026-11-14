using System.Text.RegularExpressions;

namespace AiCoachBff.Api.Services;

/// <summary>
/// Extrator do link de mídia no texto do coach (R20 F2): os fluxos que entregam mídia
/// (PDF do cronograma, relatório mensal, foto) escrevem o link RELATIVO
/// "/chat/media/{id}" — ex.: "PDF do cronograma pronto: /chat/media/media-f836…"
/// (ExportWorkoutPdfFlowHandler/PeriodReportRequestFlowHandler da API). No WhatsApp o
/// link relativo não resolve: o BFF baixa os bytes da API e entrega o documento nativo.
/// </summary>
public static partial class MediaLinkExtractor
{
    [GeneratedRegex(@"(?i)/chat/media/[a-z0-9\-]+")]
    private static partial Regex MediaLinkRegex();

    /// <summary>Primeiro link de mídia do texto (nulo se não houver). Compat — o fluxo usa FindAll.</summary>
    public static string? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = MediaLinkRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// TODOS os links de mídia do texto, distintos e na ordem. Multi-intenção é contrato
    /// da API (G19: um turno pode despachar 2+ pedidos num AgentMessage só — e dois
    /// fluxos de mídia apendam os links no MESMO texto): entregar só o 1º deixaria o
    /// resto como texto relativo morto na conversa.
    /// </summary>
    public static IReadOnlyList<string> FindAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var links = new List<string>();
        foreach (Match match in MediaLinkRegex().Matches(text))
        {
            if (!links.Contains(match.Value))
            {
                links.Add(match.Value);
            }
        }

        return links;
    }

    /// <summary>O id da mídia do link (a última parte do caminho; vazio = nulo). Link nulo = nulo.</summary>
    public static string? MediaIdOf(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        var id = link.Split('/').Last();
        return string.IsNullOrEmpty(id) ? null : id;
    }
}
