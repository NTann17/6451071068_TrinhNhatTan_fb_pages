namespace core_service.Options;

public sealed class ModerationOptions
{
    public const string SectionName = "Moderation";

    public int RepeatSpamThreshold { get; set; } = 3;

    public int RepeatWindowHours { get; set; } = 24;
}