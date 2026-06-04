namespace backend_api.Models;

public sealed class IdempotencyKey
{
    public string CommandId { get; set; } = string.Empty;

    public DateTime? ProcessedAt { get; set; }

    public string Status { get; set; } = "received";
}