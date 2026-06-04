using System.ComponentModel.DataAnnotations;

namespace core_service.Models;

public class CommentRecord
{
    [Key]
    public int Id { get; set; }

    public string CommentId { get; set; } = string.Empty;

    public string PostId { get; set; } = string.Empty;

    public string? Message { get; set; }

    public string? Intent { get; set; }

    public string? Sentiment { get; set; }

    public string Status { get; set; } = "received";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}