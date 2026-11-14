using System.Collections.Concurrent;

namespace AiCoachBff.Api.Services;

/// <summary>Resultado da verificação de acesso ao canal demo (R20, front provisório).</summary>
public enum DemoAccess
{
    /// <summary>Passa (dev sem chave configurada, ou chave batendo).</summary>
    Allowed,

    /// <summary>Chave configurada e não bateu — 403.</summary>
    Denied,

    /// <summary>Demo desligada: produção sem Demo:AccessKey configurada — 404
    /// (fail-closed: o default é NÃO existir endpoint público sem trava).</summary>
    Disabled,
}

/// <summary>Regra única de acesso do /demo — puro pra testar. A chave não é segredo
/// caro (não abre porta pra API nem pra Meta): é a trava do canal de TESTES público.
/// Ela vai no link (?k=...) e chega por header X-Demo-Key.</summary>
public static class DemoAuthorization
{
    public static DemoAccess Check(string? configuredKey, string? providedKey, bool isDevelopment)
    {
        if (string.IsNullOrEmpty(configuredKey))
        {
            // Sem chave: só Development é aberto. Fora dele, o endpoint nem existe —
            // não há página pública solta sem trava.
            return isDevelopment ? DemoAccess.Allowed : DemoAccess.Disabled;
        }

        return string.Equals(configuredKey, providedKey, StringComparison.Ordinal)
            ? DemoAccess.Allowed
            : DemoAccess.Denied;
    }
}

/// <summary>Limitador de rajada do /demo (janela fixa em memória, por processo — a BFF
/// roda min 1 max 2 réplicas; o dobro de janela só relaxa, não abre brecha séria).
/// Chave = ÚLTIMO segmento do X-Forwarded-For (o ingress do Container Apps anexa o IP
/// real ao final da lista; os anteriores o cliente pode forjar).
/// Teto de entradas (review R20): dicionário inflado por chaves forjadas é zerado de
/// vez — O(1) e honesto (todo mundo recomeça a própria janela, inclusive o atacante);
/// nada de cópia O(n) do dicionário por request.</summary>
public sealed class DemoLimiter
{
    private sealed record Bucket(long Start, int Count);

    private const int TetoEntradas = 10_000;

    private readonly ConcurrentDictionary<string, Bucket> _windows = new();

    /// <summary>true = pode passar (incrementa a cota na janela corrente).</summary>
    public bool Allow(string key, DateTimeOffset now, int limit, TimeSpan window)
    {
        var bucketStart = now.UtcTicks / window.Ticks;
        if (_windows.Count > TetoEntradas)
        {
            _windows.Clear();
        }

        var bucket = _windows.AddOrUpdate(
            key,
            _ => new Bucket(bucketStart, 1),
            (_, atual) => atual.Start == bucketStart ? atual with { Count = atual.Count + 1 } : new Bucket(bucketStart, 1));
        return bucket.Count <= limit;
    }
}
