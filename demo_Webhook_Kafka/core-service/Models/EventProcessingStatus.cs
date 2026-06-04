using System.ComponentModel.DataAnnotations;

namespace core_service.Models;

public class EventProcessingStatus
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EventRecordId { get; set; } = string.Empty;
    public string? FromState { get; set; }
    public string ToState { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
