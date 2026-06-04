using webhook_service.Models;

namespace webhook_service.Services;

public interface IFacebookGraphApiClient
{
    Task<MetaPageInfo> GetPageInfoAsync(CancellationToken cancellationToken);

    Task<MetaSubscriptionResult> SubscribePageAsync(string? subscribedFields, CancellationToken cancellationToken);

    Task<IReadOnlyList<MetaSubscribedAppInfo>> GetSubscribedAppsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FacebookPostDto>> GetPostsAsync(int limit, CancellationToken cancellationToken);

    Task<FacebookPostDto> CreatePostAsync(string message, CancellationToken cancellationToken);

    Task<IReadOnlyList<FacebookCommentDto>> GetCommentsAsync(string postId, int limit, CancellationToken cancellationToken);
}