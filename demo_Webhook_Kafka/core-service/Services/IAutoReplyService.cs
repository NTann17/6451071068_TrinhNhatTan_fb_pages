namespace core_service.Services;

public interface IAutoReplyService
{
    /// <summary>
    /// Return an auto-reply message for a given sentiment ("positive", "neutral", "negative").
    /// Returns null when no auto-reply should be sent (e.g. neutral or suppressed).
    /// </summary>
    string? GetReplyForSentiment(string? sentiment);
}
