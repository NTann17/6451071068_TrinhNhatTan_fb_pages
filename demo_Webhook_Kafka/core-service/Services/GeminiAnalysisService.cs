using System.Text;
using System.Text.Json;
using core_service.Models;

namespace core_service.Services;

public interface IAiAnalysisService
{
    Task<AiAnalysisResult?> AnalyzeTextAsync(string text, CancellationToken cancellationToken);
}

public class OpenAIAnalysisService : IAiAnalysisService
{
    private static readonly JsonSerializerOptions DeserializeOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static volatile bool _remoteAiDisabled;
    private static readonly CircuitBreakerState RemoteAiCircuitBreaker = new(3, TimeSpan.FromSeconds(30));

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAIAnalysisService> _logger;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly int _maxAttempts;

    public OpenAIAnalysisService(HttpClient httpClient, IConfiguration configuration, ILogger<OpenAIAnalysisService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["OpenAI:ApiKey"] ?? string.Empty;
        _model = configuration["OpenAI:Model"] ?? "gpt-4o-mini";
        _baseUrl = configuration["OpenAI:BaseUrl"] ?? "https://api.openai.com/v1";
        _maxAttempts = Math.Clamp(configuration.GetValue<int?>("OpenAI:MaxAttempts") ?? 2, 1, 3);
    }

    public async Task<AiAnalysisResult?> AnalyzeTextAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("[TEST] OpenAI API Key is not configured. Using local fallback analysis.");
            return BuildFallbackResult(text);
        }

        if (_remoteAiDisabled)
        {
            _logger.LogWarning("[TEST] Remote AI is disabled for this process. Using local fallback analysis.");
            return BuildFallbackResult(text);
        }

        if (!RemoteAiCircuitBreaker.TryBeginOperation(out var retryAfter))
        {
            _logger.LogWarning("[TEST] Remote AI circuit breaker is open for {RetryAfter}. Using local fallback analysis.", retryAfter);
            return BuildFallbackResult(text);
        }

        var url = $"{_baseUrl.TrimEnd('/')}/chat/completions";
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            var requestBody = new
            {
                model = _model,
                temperature = 0.1,
                response_format = new { type = "json_object" },
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = BuildPrompt()
                    },
                    new
                    {
                        role = "user",
                        content = text
                    }
                }
            };

            using var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(url, content, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    if (IsTerminalOpenAiFailure(response.StatusCode, responseBody))
                    {
                        _remoteAiDisabled = true;
                        _logger.LogWarning(
                            "[TEST] OpenAI terminal failure detected ({StatusCode}). Disabling remote AI and using fallback. Error: {ErrorBody}",
                            response.StatusCode,
                            responseBody);
                        return BuildFallbackResult(text);
                    }

                    RemoteAiCircuitBreaker.RecordFailure();
                    _logger.LogError("OpenAI API Error: {StatusCode} - {ErrorBody}", response.StatusCode, responseBody);
                    if (attempt < _maxAttempts)
                    {
                        continue;
                    }

                    return BuildFallbackResult(text);
                }

                using var document = JsonDocument.Parse(responseBody);

                var textResponse = ExtractCandidateText(document.RootElement);
                if (!string.IsNullOrWhiteSpace(textResponse))
                {
                    var jsonPayload = ExtractJsonPayload(textResponse);
                    if (!string.IsNullOrWhiteSpace(jsonPayload))
                    {
                        try
                        {
                            var result = JsonSerializer.Deserialize<AiAnalysisResult>(jsonPayload, DeserializeOptions);
                            if (result != null)
                            {
                                RemoteAiCircuitBreaker.RecordSuccess();
                                NormalizeResult(result);
                                _logger.LogInformation(
                                    "[TEST] OpenAI Analysis Result - IsSpam: {IsSpam}, Intent: {Intent}, Sentiment: {Sentiment}, RiskLevel: {RiskLevel}, RequiresManualReview: {RequiresReview}, ShouldHide: {ShouldHide}",
                                    result.IsSpam,
                                    result.Intent,
                                    result.Sentiment,
                                    result.RiskLevel,
                                    result.RequiresManualReview,
                                    result.ShouldHide);
                                return result;
                            }
                        }
                        catch (JsonException ex)
                        {
                            RemoteAiCircuitBreaker.RecordFailure();
                            _logger.LogError(ex, "Failed to deserialize JSON from OpenAI: {Json}", jsonPayload);
                        }
                    }
                }

                if (attempt < _maxAttempts)
                {
                    continue;
                }

                return BuildFallbackResult(text);
            }
            catch (Exception ex)
            {
                if (ex is HttpRequestException or TaskCanceledException)
                {
                    RemoteAiCircuitBreaker.RecordFailure();
                    _logger.LogWarning(ex, "OpenAI call failed on attempt {Attempt}/{MaxAttempts}. Falling back when retries finish.", attempt, _maxAttempts);
                }
                else
                {
                    _logger.LogError(ex, "Error calling OpenAI API on attempt {Attempt}/{MaxAttempts}", attempt, _maxAttempts);
                }

                if (attempt == _maxAttempts)
                {
                    return BuildFallbackResult(text);
                }
            }
        }
        return BuildFallbackResult(text);
    }

    private static bool IsTerminalOpenAiFailure(System.Net.HttpStatusCode statusCode, string responseBody)
    {
        if (statusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(responseBody) &&
            responseBody.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string BuildPrompt()
    {
                return @"Bạn là bộ phân loại bình luận tiếng Việt.
Trả về JSON thuần, không markdown, không giải thích.
Schema:
{
  ""isSpam"": true/false,
  ""spamReason"": ""string|null"",
  ""intent"": ""hỏi giá|khiếu nại / hỗ trợ|khen / tương tác tích cực|spam|khác"",
  ""sentiment"": ""positive|neutral|negative"",
  ""riskLevel"": ""low|medium|high"",
  ""requiresManualReview"": true/false,
  ""shouldHide"": true/false
}
Quy tắc: link lạ, scam, bot rõ ràng, spam lặp, quảng cáo rác => riskLevel high và shouldHide true. Sentiment chỉ được là positive, neutral hoặc negative.
Ví dụ: ""Dịch vụ rất tốt, mình sẽ quay lại"" => positive; ""Sản phẩm tạm ổn"" => neutral; ""Trải nghiệm quá tệ"" => negative.";
    }

    private static string? ExtractCandidateText(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var messageElement) &&
                messageElement.TryGetProperty("content", out var messageContentElement) &&
                messageContentElement.ValueKind == JsonValueKind.String)
            {
                return messageContentElement.GetString();
            }
        }

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            return null;
        }

        var firstCandidate = candidates[0];
        if (!firstCandidate.TryGetProperty("content", out var contentElement) ||
            !contentElement.TryGetProperty("parts", out var parts) ||
            parts.GetArrayLength() == 0)
        {
            return null;
        }

        return parts[0].GetProperty("text").GetString();
    }

    private static string? ExtractJsonPayload(string rawText)
    {
        var trimmed = rawText.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed.Trim('`');
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        return trimmed;
    }

    private static void NormalizeResult(AiAnalysisResult result)
    {
        result.Intent = string.IsNullOrWhiteSpace(result.Intent) ? "khác" : result.Intent.Trim();
        result.Sentiment = NormalizeSentiment(result.Sentiment);
        result.RiskLevel = NormalizeRiskLevel(result.RiskLevel);
        result.SpamReason = string.IsNullOrWhiteSpace(result.SpamReason) ? null : result.SpamReason.Trim();
    }

    private static string NormalizeSentiment(string? value)
    {
        var sentiment = value?.Trim().ToLowerInvariant();
        return sentiment switch
        {
            "positive" => "positive",
            "negative" => "negative",
            _ => "neutral"
        };
    }

    private static string NormalizeRiskLevel(string? value)
    {
        var riskLevel = value?.Trim().ToLowerInvariant();
        return riskLevel switch
        {
            "high" => "high",
            "medium" => "medium",
            _ => "low"
        };
    }

    private static AiAnalysisResult BuildFallbackResult(string text)
    {
        var lowered = text.ToLowerInvariant();
        var containsLink = lowered.Contains("http://", StringComparison.Ordinal) ||
                           lowered.Contains("https://", StringComparison.Ordinal) ||
                           lowered.Contains("www.", StringComparison.Ordinal);
        var spamKeywords = ContainsAny(lowered, ["scam", "lừa đảo", "nhận quà", "click ngay", "mua ngay", "quảng cáo", "sale sốc"]);
        var isSpam = containsLink || spamKeywords;

        return new AiAnalysisResult
        {
            IsSpam = isSpam,
            SpamReason = isSpam ? (containsLink ? "Chứa liên kết" : "Từ khóa spam/scam") : null,
            Intent = DetectIntent(text),
            Sentiment = DetectSentiment(text),
            RiskLevel = containsLink && spamKeywords ? "high" : (isSpam ? "medium" : "low"),
            RequiresManualReview = containsLink && spamKeywords,
            ShouldHide = isSpam
        };
    }

    private static string DetectIntent(string text)
    {
        var lowered = text.ToLowerInvariant();
        if (ContainsAny(lowered, ["giá", "bao nhiêu", "price", "cost"]))
        {
            return "hỏi giá";
        }

        if (ContainsAny(lowered, ["chưa nhận", "không nhận", "lỗi", "hỗ trợ", "khiếu nại", "hoàn tiền"]))
        {
            return "khiếu nại / hỗ trợ";
        }

        if (ContainsAny(lowered, ["hay quá", "tuyệt", "rất thích", "đẹp quá", "cảm ơn"]))
        {
            return "khen / tương tác tích cực";
        }

        if (ContainsAny(lowered, ["scam", "spam", "nhận quà", "click ngay", "mua ngay"]))
        {
            return "spam";
        }

        return "khác";
    }

    private static string DetectSentiment(string text)
    {
        var lowered = text.ToLowerInvariant();
        if (ContainsAny(lowered, ["rất tốt", "sẽ quay lại", "hỗ trợ rất nhanh", "hay quá", "tuyệt", "rất thích", "cảm ơn", "đẹp quá", "hài lòng"]))
        {
            return "positive";
        }

        if (ContainsAny(lowered, ["tạm ổn", "bình thường", "cũng được", "ok", "ổn", "vừa phải"]))
        {
            return "neutral";
        }

        if (ContainsAny(lowered, ["quá tệ", "trải nghiệm tệ", "không hài lòng", "chờ quá lâu", "chậm", "lỗi", "bực", "tệ", "hoàn tiền", "khiếu nại"]))
        {
            return "negative";
        }

        return "neutral";
    }

    private static bool ContainsAny(string text, IReadOnlyCollection<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
