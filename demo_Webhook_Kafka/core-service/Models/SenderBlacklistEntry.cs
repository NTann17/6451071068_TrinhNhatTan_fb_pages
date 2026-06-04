using System.ComponentModel.DataAnnotations;

namespace core_service.Models;

public class SenderBlacklistEntry
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string SenderId { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public int RepeatedSpamCount { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public DateTime? BlacklistedAt { get; set; }
}