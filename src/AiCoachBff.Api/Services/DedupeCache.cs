namespace AiCoachBff.Api.Services;

/// <summary>
/// Dedupe das entregas do webhook (R20 F2): a Meta re-entrega o mesmo message id quando
/// o ack não chega (ou quando o webhook é reenviado) — e reenvio do payload tem que
/// produzir UMA resposta. LRU in-memory por chave; escopo é a instância (tráfego
/// pequeno: max-replicas 2). Fila durável é follow-up (régua do plano R20).
/// </summary>
public sealed class DedupeCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new();
    private readonly int _capacity;

    public DedupeCache(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    /// <summary>Reserva a chave (true = primeira vez; false = já vista — reentrega).</summary>
    public bool TryAcquire(string key)
    {
        lock (_lock)
        {
            if (_seen.ContainsKey(key))
            {
                return false;
            }

            _seen[key] = DateTimeOffset.UtcNow;
            if (_seen.Count > _capacity)
            {
                var oldest = _seen.MinBy(pair => pair.Value).Key;
                _seen.Remove(oldest);
            }

            return true;
        }
    }
}
