using System.ComponentModel.DataAnnotations;

namespace core_service.Models;

public class ModerationReviewItem
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string EventId { get; set; } = string.Empty;

    public string SenderId { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string RiskLevel { get; set; } = "low";

    public string Status { get; set; } = "Pending";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}