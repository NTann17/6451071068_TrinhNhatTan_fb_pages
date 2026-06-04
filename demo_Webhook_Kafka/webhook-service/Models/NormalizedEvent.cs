namespace webhook_service.Models;

public sealed class NormalizedEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    public string CommandId { get; init; } = Guid.NewGuid().ToString("N");

    public string Source { get; init; } = "facebook";

    public string EventType { get; init; } = "unknown";

    public string? PageId { get; init; }

    public string? ActorId { get; init; }

    public string? ObjectId { get; init; }

    public string? Content { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public string RawPayload { get; init; } = "{}";
}