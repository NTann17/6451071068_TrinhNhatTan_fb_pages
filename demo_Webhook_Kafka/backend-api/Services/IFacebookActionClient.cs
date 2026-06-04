namespace backend_api.Services;

public interface IFacebookActionClient
{
    Task<bool> HideCommentAsync(string commentId, CancellationToken cancellationToken);

    Task<bool> ReplyToCommentAsync(string commentId, string message, CancellationToken cancellationToken);

    // Inbox messaging
    Task<bool> ReplyToMessageAsync(string recipientId, string message, CancellationToken cancellationToken);
}
