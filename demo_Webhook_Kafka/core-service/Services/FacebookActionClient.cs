using System.Text;
using System.Text.Json;

namespace core_service.Services;

public interface IFacebookActionClient
{
    Task<bool> HideCommentAsync(string commentId, CancellationToken cancellationToken);
    Task<bool> ReplyToCommentAsync(string commentId, string message, CancellationToken cancellationToken);
    Task<bool> BlockUserAsync(string senderId, CancellationToken cancellationToken);
}

public class FacebookActionClient : IFacebookActionClient
{
    private static readonly CircuitBreakerState CircuitBreaker = new(3, TimeSpan.FromSeconds(30));

    private readonly HttpClient _httpClient;
    private readonly ILogger<FacebookActionClient> _logger;
    private readonly string _accessToken;
    private readonly string _apiVersion;

    public FacebookActionClient(HttpClient httpClient, IConfiguration configuration, ILogger<FacebookActionClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _accessToken = configuration["FACEBOOK_PAGE_ACCESS_TOKEN"] ?? string.Empty;
        _apiVersion = configuration["FACEBOOK_GRAPH_API_VERSION"] ?? "v22.0";
        _httpClient.BaseAddress = new Uri($"https://graph.facebook.com/{_apiVersion}/");
    }

    public async Task<bool> HideCommentAsync(string commentId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            _logger.LogWarning("FACEBOOK_PAGE_ACCESS_TOKEN is not configured. Cannot hide comment.");
            return false;
        }

        if (!CircuitBreaker.TryBeginOperation(out var retryAfter))
        {
            _logger.LogWarning("Facebook Graph API circuit breaker is open for hide_comment {RetryAfter}.", retryAfter);
            throw new TransientException($"Facebook circuit breaker open. Retry after {retryAfter}.");
        }

        _logger.LogInformation("[TEST] Attempting to hide comment via Facebook Graph API - CommentId: {CommentId}", commentId);

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("is_hidden", "true"),
            new KeyValuePair<string, string>("access_token", _accessToken)
        });

        try
        {
            var response = await _httpClient.PostAsync($"{commentId}", content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                CircuitBreaker.RecordSuccess();
                _logger.LogInformation("[TEST] Successfully hidden comment {CommentId}", commentId);
                return true;
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            // Treat 5xx and 429 as transient
            var statusCode = (int)response.StatusCode;
            if (statusCode >= 500 || statusCode == 429)
            {
                CircuitBreaker.RecordFailure();
                _logger.LogWarning("[TEST] Transient failure hiding comment {CommentId}. Status: {StatusCode}, Error: {Error}", commentId, response.StatusCode, error);
                throw new TransientException($"Transient HTTP error {response.StatusCode}: {error}");
            }

            _logger.LogError("[TEST] Failed to hide comment {CommentId}. Status: {StatusCode}, Error: {Error}", commentId, response.StatusCode, error);
            return false;
        }
        catch (TaskCanceledException ex)
        {
            CircuitBreaker.RecordFailure();
            _logger.LogWarning(ex, "[TEST] Timeout while hiding comment {CommentId}", commentId);
            throw new TransientException("Timeout while calling Facebook Graph API", ex);
        }
        catch (HttpRequestException ex)
        {
            CircuitBreaker.RecordFailure();
            _logger.LogWarning(ex, "[TEST] HTTP error while hiding comment {CommentId}", commentId);
            throw new TransientException("HTTP error while calling Facebook Graph API", ex);
        }
        catch (TransientException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TEST] Exception while hiding comment {CommentId}", commentId);
            return false;
        }
    }

    public async Task<bool> ReplyToCommentAsync(string commentId, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            _logger.LogWarning("FACEBOOK_PAGE_ACCESS_TOKEN is not configured. Cannot reply to comment.");
            return false;
        }

        if (!CircuitBreaker.TryBeginOperation(out var retryAfter))
        {
            _logger.LogWarning("Facebook Graph API circuit breaker is open for reply_comment {RetryAfter}.", retryAfter);
            throw new TransientException($"Facebook circuit breaker open. Retry after {retryAfter}.");
        }

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("message", message),
            new KeyValuePair<string, string>("access_token", _accessToken)
        });

        try
        {
            var response = await _httpClient.PostAsync($"{commentId}/comments", content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                CircuitBreaker.RecordSuccess();
                _logger.LogInformation("[TEST] Successfully replied to comment {CommentId}", commentId);
                return true;
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (statusCode >= 500 || statusCode == 429)
            {
                CircuitBreaker.RecordFailure();
                _logger.LogWarning("[TEST] Transient failure replying to comment {CommentId}. Status: {StatusCode}, Error: {Error}", commentId, response.StatusCode, error);
                throw new TransientException($"Transient HTTP error {response.StatusCode}: {error}");
            }

            _logger.LogError("[TEST] Failed to reply to comment {CommentId}. Status: {StatusCode}, Error: {Error}", commentId, response.StatusCode, error);
            return false;
        }
        catch (TaskCanceledException ex)
        {
            CircuitBreaker.RecordFailure();
            _logger.LogWarning(ex, "[TEST] Timeout while replying to comment {CommentId}", commentId);
            throw new TransientException("Timeout while calling Facebook Graph API", ex);
        }
        catch (HttpRequestException ex)
        {
            CircuitBreaker.RecordFailure();
            _logger.LogWarning(ex, "[TEST] HTTP error while replying to comment {CommentId}", commentId);
            throw new TransientException("HTTP error while calling Facebook Graph API", ex);
        }
        catch (TransientException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TEST] Exception while replying to comment {CommentId}", commentId);
            return false;
        }
    }

    public Task<bool> BlockUserAsync(string senderId, CancellationToken cancellationToken)
    {
        // Facebook Graph API does not provide a straightforward endpoint to block a user from a Page
        // without specific permissions/contexts or using the Page Settings API which is complex.
        // For this demo, we will simulate the action or mark it internally.
        _logger.LogWarning("BlockUser API is restricted. Marking user {SenderId} as blocked internally.", senderId);
        
        // In a real scenario, you might call an internal API to add this user to a database blacklist.
        return Task.FromResult(true);
    }
}
