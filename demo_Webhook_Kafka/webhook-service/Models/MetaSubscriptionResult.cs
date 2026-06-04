namespace webhook_service.Models;

public sealed class MetaSubscriptionResult
{
    public bool Success { get; init; }

    public string SubscribedFields { get; init; } = string.Empty;
}

public sealed class MetaSubscribedAppInfo
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string> SubscribedFields { get; init; } = Array.Empty<string>();
}