using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using webhook_service.Models;

namespace webhook_service.Services;

public sealed class FacebookGraphApiClient : IFacebookGraphApiClient
{
    private readonly HttpClient _httpClient;
    private readonly FacebookOptions _options;
    private readonly ILogger<FacebookGraphApiClient> _logger;

    public FacebookGraphApiClient(HttpClient httpClient, IOptions<FacebookOptions> options, ILogger<FacebookGraphApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri("https://graph.facebook.com/");
        }
    }

    public async Task<MetaPageInfo> GetPageInfoAsync(CancellationToken cancellationToken)
    {
        ValidateConfiguration();

        var url = $"{_options.GraphApiVersion}/{Uri.EscapeDataString(_options.PageId)}?fields=id,name&access_token={Uri.EscapeDataString(_options.PageAccessToken)}";
        using var json = await SendJsonAsync(HttpMethod.Get, url, null, cancellationToken);
        var root = json.RootElement;

        return new MetaPageInfo
        {
            Id = root.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty
        };
    }

    public async Task<MetaSubscriptionResult> SubscribePageAsync(string? subscribedFields, CancellationToken cancellationToken)
    {
        ValidateConfiguration();

        var fields = string.IsNullOrWhiteSpace(subscribedFields) ? _options.SubscribedFields : subscribedFields.Trim();

        var url = $"{_options.GraphApiVersion}/{Uri.EscapeDataString(_options.PageId)}/subscribed_apps" +
                  $"?access_token={Uri.EscapeDataString(_options.PageAccessToken)}";

        using var json = await SendFormAsync(HttpMethod.Post, url, new[]
        {
            new KeyValuePair<string, string>("subscribed_fields", fields)
        }, cancellationToken);
        var root = json.RootElement;
        var success = root.TryGetProperty("success", out var successElement) &&
                      successElement.ValueKind == JsonValueKind.True;

        return new MetaSubscriptionResult
        {
            Success = success,
            SubscribedFields = fields
        };
    }

    public async Task<IReadOnlyList<MetaSubscribedAppInfo>> GetSubscribedAppsAsync(CancellationToken cancellationToken)
    {
        ValidateConfiguration();

        var url = $"{_options.GraphApiVersion}/{Uri.EscapeDataString(_options.PageId)}/subscribed_apps" +
                  $"?fields=id,name,subscribed_fields&access_token={Uri.EscapeDataString(_options.PageAccessToken)}";

        using var json = await SendJsonAsync(HttpMethod.Get, url, null, cancellationToken);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MetaSubscribedAppInfo>();
        }

        var apps = new List<MetaSubscribedAppInfo>();
        foreach (var item in data.EnumerateArray())
        {
            var subscribedFields = new List<string>();
            if (item.TryGetProperty("subscribed_fields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fieldsElement.EnumerateArray())
                {
                    if (field.ValueKind == JsonValueKind.String)
                    {
                        var value = field.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            subscribedFields.Add(value);
                        }
                    }
                }
            }

            apps.Add(new MetaSubscribedAppInfo
            {
                Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                SubscribedFields = subscribedFields
            });
        }

        return apps;
    }

    public async Task<IReadOnlyList<FacebookPostDto>> GetPostsAsync(int limit, CancellationToken cancellationToken)
    {
        ValidateConfiguration();

        var url = $"{_options.GraphApiVersion}/{Uri.EscapeDataString(_options.PageId)}/posts" +
                  $"?fields=id,message,created_time,permalink_url,comments.summary(true).limit(0)&limit={limit}&access_token={Uri.EscapeDataString(_options.PageAccessToken)}";

        using var json = await SendJsonAsync(HttpMethod.Get, url, null, cancellationToken);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<FacebookPostDto>();
        }

        var posts = new List<FacebookPostDto>();
        foreach (var item in data.EnumerateArray())
        {
            posts.Add(new FacebookPostDto(
                item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("message", out var message) ? message.GetString() : null,
                ParseDateTimeOffset(item, "created_time"),
                item.TryGetProperty("permalink_url", out var permalinkUrl) ? permalinkUrl.GetString() : null,
                TryGetCommentsCount(item)));
        }

        return posts;
    }

    public async Task<FacebookPostDto> CreatePostAsync(string message, CancellationToken cancellationToken)
    {
        ValidateConfiguration();

        var trimmedMessage = message.Trim();
        var url = $"{_options.GraphApiVersion}/{Uri.EscapeDataString(_options.PageId)}/feed?access_token={Uri.EscapeDataString(_options.PageAccessToken)}";

        using var json = await SendFormAsync(HttpMethod.Post, url, new[]
        {
            new KeyValuePair<string, string>("message", trimmedMessage)
        }, cancellationToken);

        var root = json.RootElement;
        return new FacebookPostDto(
            root.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            trimmedMessage,
            DateTimeOffset.UtcNow,
            null,
            null);
    }

    public async Task<IReadOnlyList<FacebookCommentDto>> GetCommentsAsync(string postId, int limit, CancellationToken cancellationToken)
    {
        ValidateConfiguration();

        var url = $"{_options.GraphApiVersion}/{Uri.EscapeDataString(postId)}/comments" +
                  $"?fields=id,message,created_time,from{{id,name}}&limit={limit}&access_token={Uri.EscapeDataString(_options.PageAccessToken)}";

        using var json = await SendJsonAsync(HttpMethod.Get, url, null, cancellationToken);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<FacebookCommentDto>();
        }

        var comments = new List<FacebookCommentDto>();
        foreach (var item in data.EnumerateArray())
        {
            string? fromId = null;
            string? fromName = null;
            if (item.TryGetProperty("from", out var from))
            {
                fromId = from.TryGetProperty("id", out var fromIdElement) ? fromIdElement.GetString() : null;
                fromName = from.TryGetProperty("name", out var fromNameElement) ? fromNameElement.GetString() : null;
            }

            comments.Add(new FacebookCommentDto(
                item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("message", out var message) ? message.GetString() : null,
                ParseDateTimeOffset(item, "created_time"),
                fromId,
                fromName));
        }

        return comments;
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.PageId))
        {
            throw new InvalidOperationException("Facebook:PageId is required.");
        }

        if (string.IsNullOrWhiteSpace(_options.PageAccessToken))
        {
            throw new InvalidOperationException("Facebook:PageAccessToken is required.");
        }

        if (string.IsNullOrWhiteSpace(_options.GraphApiVersion))
        {
            throw new InvalidOperationException("Facebook:GraphApiVersion is required.");
        }
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = content
        };

        LogRequest(method, url, content is null ? null : "(payload omitted)");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LogResponse(method, url, response.StatusCode, body);

        if (!response.IsSuccessStatusCode)
        {
            throw BuildException(url, response.StatusCode, body);
        }

        return JsonDocument.Parse(body);
    }

    private async Task<JsonDocument> SendFormAsync(HttpMethod method, string url, IEnumerable<KeyValuePair<string, string>> formValues, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(formValues);
        using var request = new HttpRequestMessage(method, url)
        {
            Content = content
        };

        LogRequest(method, url, string.Join('&', formValues.Select(kvp => $"{kvp.Key}=<redacted>")));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LogResponse(method, url, response.StatusCode, body);

        if (!response.IsSuccessStatusCode)
        {
            throw BuildException(url, response.StatusCode, body);
        }

        return JsonDocument.Parse(body);
    }

    private FacebookApiException BuildException(string endpoint, System.Net.HttpStatusCode statusCode, string body)
    {
        var friendlyMessage = statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Facebook access token is invalid or expired.",
            System.Net.HttpStatusCode.Forbidden => "Facebook rejected the request because the token lacks required permissions.",
            System.Net.HttpStatusCode.NotFound => "Facebook resource was not found.",
            System.Net.HttpStatusCode.TooManyRequests => "Facebook rate limit was reached.",
            _ when (int)statusCode >= 500 => "Facebook Graph API is temporarily unavailable.",
            _ => "Facebook Graph API returned an error."
        };

        return new FacebookApiException(statusCode, friendlyMessage, body, SanitizeEndpoint(endpoint));
    }

    private void LogRequest(HttpMethod method, string endpoint, string? summary)
    {
        _logger.LogInformation("Facebook request -> {Method} {Endpoint} {Summary}", method.Method, SanitizeEndpoint(endpoint), summary ?? string.Empty);
    }

    private void LogResponse(HttpMethod method, string endpoint, System.Net.HttpStatusCode statusCode, string body)
    {
        var preview = body.Length <= 400 ? body : body[..400] + "...";
        _logger.LogInformation("Facebook response <- {Method} {Endpoint} {StatusCode} {Body}", method.Method, SanitizeEndpoint(endpoint), (int)statusCode, preview);
    }

    private static string SanitizeEndpoint(string endpoint)
    {
        var redacted = endpoint;
        const string tokenPrefix = "access_token=";
        var tokenIndex = redacted.IndexOf(tokenPrefix, StringComparison.OrdinalIgnoreCase);
        if (tokenIndex >= 0)
        {
            var tokenEnd = redacted.IndexOf('&', tokenIndex);
            if (tokenEnd >= 0)
            {
                redacted = redacted[..tokenIndex] + tokenPrefix + "<redacted>" + redacted[tokenEnd..];
            }
            else
            {
                redacted = redacted[..tokenIndex] + tokenPrefix + "<redacted>";
            }
        }

        return redacted;
    }

    private static DateTimeOffset? ParseDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(property.GetString(), out var parsed) ? parsed : null;
    }

    private static int? TryGetCommentsCount(JsonElement element)
    {
        if (!element.TryGetProperty("comments", out var comments))
        {
            return null;
        }

        if (comments.TryGetProperty("summary", out var summary) && summary.TryGetProperty("total_count", out var totalCount) && totalCount.TryGetInt32(out var count))
        {
            return count;
        }

        return null;
    }
}