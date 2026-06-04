namespace webhook_service.Models;

public sealed record FacebookPostDto(
    string Id,
    string? Message,
    DateTimeOffset? CreatedTime,
    string? PermalinkUrl,
    int? CommentsCount);