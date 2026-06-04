using Microsoft.Extensions.Configuration;

namespace core_service.Services;

public class AutoReplyService : IAutoReplyService
{
    private readonly string _positiveTemplate;
    private readonly string _negativeTemplate;

    public AutoReplyService(IConfiguration configuration)
    {
        // Allow overriding templates via configuration, otherwise use sane defaults
        _positiveTemplate = configuration["AutoReply:Positive"] ?? "Cảm ơn bạn đã ủng hộ shop!";
        _negativeTemplate = configuration["AutoReply:Negative"] ?? "Rất xin lỗi vì trải nghiệm chưa tốt, bên mình sẽ kiểm tra ngay.";
    }

    public string? GetReplyForSentiment(string? sentiment)
    {
        if (string.IsNullOrWhiteSpace(sentiment)) return null;
        return sentiment.Trim().ToLowerInvariant() switch
        {
            "positive" => _positiveTemplate,
            "negative" => _negativeTemplate,
            // neutral/unknown: theo luật chỉ trả lời với positive/negative
            _ => null
        };
    }

}
