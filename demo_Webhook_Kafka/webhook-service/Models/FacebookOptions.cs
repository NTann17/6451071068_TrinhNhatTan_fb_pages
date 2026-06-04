namespace webhook_service.Models;

public sealed class FacebookOptions
{
    public const string SectionName = "Facebook";

    public string AppId { get; set; } = string.Empty;

    public string AppSecret { get; set; } = string.Empty;

    public string VerifyToken { get; set; } = string.Empty;

    public string PageId { get; set; } = string.Empty;

    public string PageAccessToken { get; set; } = string.Empty;

    public string GraphApiVersion { get; set; } = "v22.0";

    public string SubscribedFields { get; set; } = "feed,messages";
}