using Microsoft.Extensions.Options;
using webhook_service.Models;

namespace backend_api.Services;

public sealed class FacebookActionClient : IFacebookActionClient
{
    private readonly HttpClient _httpClient;
    private readonly FacebookOptions _options;
    private readonly ILogger<FacebookActionClient> _logger;

    public FacebookActionClient(HttpClient httpClient, IOptions<FacebookOptions> options, ILogger<FacebookActionClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri($"https://graph.facebook.com/{_options.GraphApiVersion}/");
        }
    }

    public Task<bool> HideCommentAsync(string commentId, CancellationToken cancellationToken)
    {
        return SendActionAsync(commentId, "hide_comment", new[]
        {
            new KeyValuePair<string, string>("is_hidden", "true"),
            new KeyValuePair<string, string>("access_token", _options.PageAccessToken)
        }, cancellationToken);
    }

    public Task<bool> ReplyToCommentAsync(string commentId, string message, CancellationToken cancellationToken)
    {
        return SendActionAsync($"{commentId}/comments", "reply_comment", new[]
        {
            new KeyValuePair<string, string>("message", message),
            new KeyValuePair<string, string>("access_token", _options.PageAccessToken)
        }, cancellationToken);
    }

    public Task<bool> ReplyToMessageAsync(string recipientId, string message, CancellationToken cancellationToken)
    {
        // Inbox messaging: send a text message to the PSID (recipientId)
        // Endpoint: /{recipientId}/messages
        // Facebook Graph messaging API requires `recipient` parameter.
        // Send as: recipient={"id":"<PSID>"}

        if (string.IsNullOrWhiteSpace(recipientId))
        {
            _logger.LogWarning("ReplyToMessageAsync called with empty recipientId. Skip.");
            return Task.FromResult(false);
        }

        var messageObject = new { text = message };
        var messageJson = System.Text.Json.JsonSerializer.Serialize(messageObject);

        var recipientObject = new { id = recipientId };
        var recipientJson = System.Text.Json.JsonSerializer.Serialize(recipientObject);

        // Inbox messaging endpoint. Use /me/messages (common) with recipient parameter.
        return SendActionAsync($"me/messages", "reply_message", new[]
        {
            new KeyValuePair<string, string>("recipient", recipientJson),
            new KeyValuePair<string, string>("message", messageJson),
            new KeyValuePair<string, string>("access_token", _options.PageAccessToken)
        }, cancellationToken);
    }



    private async Task<bool> SendActionAsync(string endpoint, string actionName, IEnumerable<KeyValuePair<string, string>> formValues, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.PageAccessToken))
        {
            _logger.LogWarning("Facebook page access token is missing. Cannot execute {ActionName}.", actionName);
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(formValues)
        };

        // DEBUG: log request target for tracing recipient/message failures
        // (avoid logging access_token)
        try
        {
            var recipientKey = formValues.FirstOrDefault(kv => kv.Key == "recipient").Value;
            var msgKey = formValues.FirstOrDefault(kv => kv.Key == "message").Value;

            _logger.LogInformation(
                "[DEBUG][FacebookActionClient] Sending action={ActionName} endpoint={Endpoint} recipientJson={RecipientJson} messageText={MessageText}",
                actionName,
                endpoint,
                recipientKey,
                msgKey);
        }
        catch
        {
            // best-effort debug only
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                throw new TransientException($"Facebook Graph API returned {(int)response.StatusCode} for {actionName}: {body}");
            }

            _logger.LogWarning("Facebook {ActionName} failed with status {StatusCode}: {Body}", actionName, (int)response.StatusCode, body.Length <= 2000 ? body : body[..2000] + "...");
            return false;
        }
        catch (TaskCanceledException ex)
        {
            throw new TransientException($"Timeout while executing {actionName}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new TransientException($"HTTP error while executing {actionName}", ex);
        }
    }
}