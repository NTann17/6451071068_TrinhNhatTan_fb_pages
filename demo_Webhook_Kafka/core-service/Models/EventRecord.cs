using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace core_service.Models;

public class EventRecord
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EventId { get; set; } = string.Empty; // From Facebook webhook

    [NotMapped]
    public string CommandId
    {
        get => EventId;
        set => EventId = value;
    }

    public string SenderId { get; set; } = string.Empty;

    // For message events from webhook-service:
    // - SenderId: ActorId (message.sender.id)
    // - RecipientId: ObjectId (message.recipient.id)
    // Needed to send reply_message correctly to Graph API.
    [NotMapped]
    public string RecipientId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string State { get; set; } = "Received"; // Received, Processing, Processed, Failed
    public string? Intent { get; set; }
    public string? Sentiment { get; set; }
    public bool IsSpam { get; set; }
    public string? SpamReason { get; set; }
    public string? ActionTaken { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
