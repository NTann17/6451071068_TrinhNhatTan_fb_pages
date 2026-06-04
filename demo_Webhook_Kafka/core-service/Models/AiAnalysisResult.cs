using System.Text.Json.Serialization;

namespace core_service.Models;

public class AiAnalysisResult
{
    [JsonPropertyName("isSpam")]
    public bool IsSpam { get; set; }

    [JsonPropertyName("spamReason")]
    public string? SpamReason { get; set; }

    [JsonPropertyName("intent")]
    public string? Intent { get; set; }

    [JsonPropertyName("sentiment")]
    public string? Sentiment { get; set; } // positive, neutral, negative

    [JsonPropertyName("riskLevel")]
    public string? RiskLevel { get; set; } // low, medium, high

    [JsonPropertyName("requiresManualReview")]
    public bool RequiresManualReview { get; set; }

    [JsonPropertyName("shouldHide")]
    public bool ShouldHide { get; set; }
}
