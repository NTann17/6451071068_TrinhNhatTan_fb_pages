namespace DemoWebhookKafka.Contracts;

public static class KafkaTopics
{
    public const string RawEvents = "raw_events";
    public const string ReplyCommands = "reply_commands";
    public const string SendRetry = "send_retry";
    public const string SendFailed = "send_failed";
    public const string DeadLetter = "dead_letter";
}

public sealed record FacebookCommandMessage
{
    public string CommandId { get; init; } = Guid.NewGuid().ToString("N");

    public string SourceEventId { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string? PageId { get; init; }

    public string? TargetId { get; init; }

    public string? Message { get; init; }

    public string? ActorId { get; init; }

    public string? Reason { get; init; }

    public int RetryCount { get; init; }

    public string? FailureReason { get; init; }

    public string Topic { get; init; } = KafkaTopics.ReplyCommands;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastAttemptAt { get; init; }
}