namespace core_service.Services;

public sealed record ModerationDecision(
    string ActionTaken,
    bool ShouldHideComment,
    bool RequiresManualReview,
    bool RequiresBlacklist,
    bool SuppressAutoReply,
    string RiskLevel,
    IReadOnlyCollection<string> Reasons);

public sealed record ContentSignals(
    bool ContainsLink,
    bool RepeatedMessage,
    bool ScamKeyword,
    bool BotLike,
    string NormalizedMessage,
    int RecentSpamCount,
    int RecentMessageCount)
{
    public bool IsSpam => ContainsLink || RepeatedMessage || ScamKeyword || BotLike;
}

public sealed record SenderReputationSnapshot(
    bool IsBlacklisted,
    int RecentSpamCount,
    int RecentMessageCount,
    int RepeatedMessageCount,
    core_service.Models.SenderBlacklistEntry? BlacklistEntry)
{
    public static SenderReputationSnapshot Empty { get; } = new(false, 0, 0, 0, null);
}