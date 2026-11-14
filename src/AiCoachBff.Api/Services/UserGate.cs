using System.Collections.Concurrent;

namespace AiCoachBff.Api.Services;

/// <summary>
/// Fila por usuário (R20): o webhook da Meta entrega rajadas — várias messages no MESMO
/// payload ou payloads seguidos — e a espera do coach (pior caso ~3min) faz a 2ª mensagem
/// chegar DENTRO do turno da 1ª. Dois send-message do MESMO userId em paralelo corrompem
/// o modelo linear de sessão da API (insert de sessão duplicado, corpus fora de ordem,
/// estado dos fluxos misturado). A fila garante um turno de cada vez por waId, na ordem
/// de entrega da Meta.
///
/// Singleton de propósito: o estado tem que viver entre os requests — o WebhookService
/// é scoped (um por mensagem entregue). O dicionário cresce por waId e não descarta;
/// tráfego pequeno aceito (mesmo comprometimento do DedupeCache — fila durável é follow-up).
/// </summary>
public sealed class UserGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    /// <summary>Fila (max 1 por vez) do usuário; os demais turnos esperam a vez.</summary>
    public SemaphoreSlim GetGate(string waId) => _gates.GetOrAdd(waId, _ => new SemaphoreSlim(1, 1));
}
