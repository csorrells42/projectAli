using System.Text.Json;

namespace Ali.Modules.Orchestration.Contracts;

public sealed record TurnEventDraft
{
    public TurnEventDraft(string eventType, JsonElement data)
    {
        EventType = string.IsNullOrWhiteSpace(eventType)
            ? throw new ArgumentException("An event type is required.", nameof(eventType))
            : eventType;
        Data = data.Clone();
    }

    public string EventType { get; }

    public JsonElement Data { get; }
}

public sealed record TurnEvent(
    TurnIdentity Identity,
    long Cursor,
    string EventId,
    string EventType,
    DateTimeOffset RecordedAtUtc,
    JsonElement Data,
    string Checksum);
