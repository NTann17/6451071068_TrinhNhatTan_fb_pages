using System.Globalization;
using System.Text.Json;
using webhook_service.Models;

namespace webhook_service.Services;

public sealed class FacebookPayloadNormalizer : IFacebookPayloadNormalizer
{
    public IReadOnlyList<NormalizedEvent> Normalize(JsonElement payload)
    {
        var results = new List<NormalizedEvent>();

        ExtractDeveloperSampleFeedEvent(payload, results);

        if (!payload.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            var pageId = TryGetString(entry, "id");
            var occurredAt = ParseFacebookTimestamp(entry.TryGetProperty("time", out var timeElement) ? timeElement : default);

            ExtractCommentEvents(entry, pageId, occurredAt, results);
            ExtractMessagingEvents(entry, pageId, occurredAt, results);
        }

        return results;
    }

    private static void ExtractDeveloperSampleFeedEvent(JsonElement payload, ICollection<NormalizedEvent> output)
    {
        if (!payload.TryGetProperty("sample", out var sample) || sample.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!sample.TryGetProperty("field", out var fieldElement) || fieldElement.GetString() != "feed")
        {
            return;
        }

        if (!sample.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var item = TryGetString(value, "item");
        var verb = TryGetString(value, "verb");
        var eventType = item ?? verb ?? "feed";

        var eventId = TryGetString(value, "comment_id")
                      ?? TryGetString(value, "post_id")
                      ?? TryGetString(value, "id")
                      ?? Guid.NewGuid().ToString("N");

        var actorId = TryGetString(value, "from", "id");
        var objectId = TryGetString(value, "post_id")
                       ?? TryGetString(value, "parent_id")
                       ?? TryGetString(value, "object_id")
                       ?? TryGetString(value, "id");
        var content = TryGetString(value, "message") ?? TryGetString(value, "story") ?? TryGetString(value, "title");
        var occurredAt = ParseFacebookTimestamp(value.TryGetProperty("created_time", out var createdTime) ? createdTime : default)
                         ?? DateTimeOffset.UtcNow;

        output.Add(new NormalizedEvent
        {
            EventId = eventId,
            CommandId = eventId,
            Source = "facebook",
            EventType = eventType,
            PageId = actorId,
            ActorId = actorId,
            ObjectId = objectId,
            Content = content,
            OccurredAt = occurredAt,
            RawPayload = sample.GetRawText()
        });
    }

    private static void ExtractCommentEvents(
        JsonElement entry,
        string? pageId,
        DateTimeOffset? fallbackOccurredAt,
        ICollection<NormalizedEvent> output)
    {
        if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var change in changes.EnumerateArray())
        {
            if (!change.TryGetProperty("field", out var fieldElement) || fieldElement.GetString() != "feed")
            {
                continue;
            }

            if (!change.TryGetProperty("value", out var value))
            {
                continue;
            }

            var item = TryGetString(value, "item");
            var verb = TryGetString(value, "verb");
            var eventType = item ?? verb ?? "feed";

            var eventId = TryGetString(value, "comment_id")
                          ?? TryGetString(value, "post_id")
                          ?? TryGetString(value, "id")
                          ?? Guid.NewGuid().ToString("N");

            var actorId = TryGetString(value, "from", "id");
            var objectId = TryGetString(value, "post_id")
                           ?? TryGetString(value, "parent_id")
                           ?? TryGetString(value, "object_id")
                           ?? TryGetString(value, "id");
            var content = TryGetString(value, "message") ?? TryGetString(value, "story") ?? TryGetString(value, "title");
            var occurredAt = ParseFacebookTimestamp(value.TryGetProperty("created_time", out var createdTime) ? createdTime : default)
                             ?? fallbackOccurredAt
                             ?? DateTimeOffset.UtcNow;

            output.Add(new NormalizedEvent
            {
                EventId = eventId,
                CommandId = eventId,
                Source = "facebook",
                EventType = eventType,
                PageId = pageId,
                ActorId = actorId,
                ObjectId = objectId,
                Content = content,
                OccurredAt = occurredAt,
                RawPayload = value.GetRawText()
            });
        }
    }

    private static void ExtractMessagingEvents(
        JsonElement entry,
        string? pageId,
        DateTimeOffset? fallbackOccurredAt,
        ICollection<NormalizedEvent> output)
    {
        if (!entry.TryGetProperty("messaging", out var messagingArray) || messagingArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var messaging in messagingArray.EnumerateArray())
        {
            var senderId = TryGetString(messaging, "sender", "id");
            var recipientId = TryGetString(messaging, "recipient", "id");
            var messageText = TryGetString(messaging, "message", "text");
            var messageMid = TryGetString(messaging, "message", "mid") ?? Guid.NewGuid().ToString("N");
            var occurredAt = ParseFacebookTimestamp(messaging.TryGetProperty("timestamp", out var timestamp) ? timestamp : default)
                             ?? fallbackOccurredAt
                             ?? DateTimeOffset.UtcNow;

            if (messageText is null)
            {
                continue;
            }

            output.Add(new NormalizedEvent
            {
                EventId = messageMid,
                CommandId = messageMid,
                Source = "facebook",
                EventType = "message",
                PageId = pageId ?? recipientId,
                ActorId = senderId,
                ObjectId = recipientId,
                Content = messageText,
                OccurredAt = occurredAt,
                RawPayload = messaging.GetRawText()
            });
        }
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static DateTimeOffset? ParseFacebookTimestamp(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt64(out var unixMillis))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMillis);
            }

            if (element.TryGetDouble(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
            }
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            {
                if (value.Length >= 13)
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(unix);
                }

                return DateTimeOffset.FromUnixTimeSeconds(unix);
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }
}