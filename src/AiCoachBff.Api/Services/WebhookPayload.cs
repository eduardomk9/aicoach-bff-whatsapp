using System.Text.Json.Serialization;

namespace AiCoachBff.Api.Services;

// R20 F2: os campos do webhook da Meta chegam em snake_case — no .NET 8 o
// System.Text.Json não tem policy SnakeCase nativa, então o contrato fica
// EXPLÍCITO por JsonPropertyName (inviolável: é o contrato da Meta).

/// <summary>Payload entregue pela Meta (verify + messages + statuses).</summary>
public sealed record WebhookPayload(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("entry")] WebhookEntry[] Entry);

public sealed record WebhookEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("changes")] WebhookChange[] Changes);

public sealed record WebhookChange(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("value")] WebhookValue Value);

public sealed record WebhookValue(
    [property: JsonPropertyName("contacts")] InboundContact[] Contacts,
    [property: JsonPropertyName("messages")] InboundMessage[] Messages,
    [property: JsonPropertyName("statuses")] InboundStatus[] Statuses);

public sealed record InboundContact(
    [property: JsonPropertyName("profile")] InboundProfile Profile,
    [property: JsonPropertyName("wa_id")] string WaId);

public sealed record InboundProfile(
    [property: JsonPropertyName("name")] string? Name);

public sealed record InboundMessage(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] InboundText Text);

public sealed record InboundText(
    [property: JsonPropertyName("body")] string Body);

/// <summary>Recibo de entrega/read da Meta — ignore (ack 200 e nada).</summary>
public sealed record InboundStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("recipient_id")] string RecipientId);
