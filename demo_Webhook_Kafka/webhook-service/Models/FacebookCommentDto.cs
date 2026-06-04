namespace webhook_service.Models;

public sealed record FacebookCommentDto(
    string Id,
    string? Message,
    DateTimeOffset? CreatedTime,
    string? FromId,
    string? FromName);