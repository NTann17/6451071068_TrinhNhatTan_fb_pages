using System.Text.Json;
using core_service.Data;
using core_service.Models;
using core_service.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DemoWebhookKafka.Contracts;

namespace core_service.Services;

public class EventProcessingPipeline
{
    private static readonly HashSet<string> HighRiskKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "scam",
        "lừa đảo",
        "trúng thưởng",
        "click ngay",
        "nhận quà",
        "giải ngân",
        "nạp tiền",
        "ưu đãi sốc"
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAiAnalysisService _aiAnalysisService;
    private readonly IReplyCommandPublisher _replyCommandPublisher;
    private readonly IAutoReplyService _autoReplyService;
    private readonly ILogger<EventProcessingPipeline> _logger;
    private readonly ModerationOptions _moderationOptions;

    public EventProcessingPipeline(
        IServiceScopeFactory scopeFactory,
        IAiAnalysisService aiAnalysisService,
        IReplyCommandPublisher replyCommandPublisher,
        IAutoReplyService autoReplyService,
        IOptions<ModerationOptions> moderationOptions,
        ILogger<EventProcessingPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _aiAnalysisService = aiAnalysisService;
        _replyCommandPublisher = replyCommandPublisher;
        _autoReplyService = autoReplyService;
        _logger = logger;
        _moderationOptions = moderationOptions.Value;
    }

    public async Task ProcessEventAsync(string messageValue, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        EventRecord eventRecord;
        var eventType = "unknown";
        var postId = string.Empty;
        try
        {
            using var jsonDoc = JsonDocument.Parse(messageValue);
            var root = jsonDoc.RootElement;
            
            eventType = GetStringProperty(root, "EventType", "eventType") ?? "unknown";
            var commandId = GetStringProperty(root, "CommandId", "commandId", "EventId", "eventId", "Id", "id") ?? Guid.NewGuid().ToString();
            var message = GetStringProperty(root, "Content", "content", "Message", "message") ?? string.Empty;
            var senderId = GetStringProperty(root, "ActorId", "actorId", "SenderId", "senderId") ?? string.Empty;
            var objectId = GetStringProperty(root, "ObjectId", "objectId", "PostId", "postId") ?? string.Empty;

            // For message events, ObjectId maps to message.recipient.id (recipient PSID/page id depending on payload).
            // For comment events, ObjectId is treated as postId used in CommentRecord.
            postId = objectId;

            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(commandId))
            {
                _logger.LogWarning("Event payload is missing required fields. Skipping.");
                return;
            }

            var existingRecord = await dbContext.EventRecords
                .FirstOrDefaultAsync(x => x.EventId == commandId, cancellationToken)
                ;

            var isNewRecord = existingRecord == null;
            eventRecord = existingRecord ?? new EventRecord
                {
                    EventId = commandId,
                    SenderId = senderId ?? string.Empty,
                    RecipientId = objectId ?? string.Empty,
                    Message = message,
                    State = "Received"
                };

            if (eventType.Equals("comment", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(postId))
            {
                await UpsertCommentRecordAsync(dbContext, eventRecord, postId, "received", cancellationToken);
            }

            if (eventRecord.State == "Processed")
            {
                _logger.LogInformation("Event {EventId} already processed. Skipping duplicate.", commandId);
                return;
            }

            eventRecord.SenderId = senderId ?? eventRecord.SenderId;
            eventRecord.RecipientId = objectId ?? eventRecord.RecipientId;
            eventRecord.Message = message;
            eventRecord.UpdatedAt = DateTime.UtcNow;

            if (isNewRecord)
            {
                dbContext.EventRecords.Add(eventRecord);
                await dbContext.SaveChangesAsync(cancellationToken);
                await AddEventStatusAsync(dbContext, eventRecord, "Received", "Enqueued from Kafka");
            }
            else if (eventRecord.State == "Failed")
            {
                await AddEventStatusAsync(dbContext, eventRecord, "Recovered", "Retrying failed command");
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse message from Kafka: {MessageValue}", messageValue);
            return; // Dead letter in real world
        }

        try
        {
            var normalizedMessage = NormalizeContent(eventRecord.Message);
            // mark processing
            await AddEventStatusAsync(dbContext, eventRecord, "Processing", "Started processing");
            _logger.LogInformation("[TEST] Processing event - Type: {EventType}, SenderId: {SenderId}, Message: {Message}", eventType, eventRecord.SenderId, eventRecord.Message);
            
            var reputation = await GetSenderReputationAsync(dbContext, eventRecord.SenderId, normalizedMessage, cancellationToken);
            _logger.LogInformation("[TEST] Reputation lookup - IsBlacklisted: {IsBlacklisted}, RecentSpamCount: {SpamCount}, RecentMessages: {RecentMessages}, RepeatedMessageCount: {Repeated}",
                reputation.IsBlacklisted,
                reputation.RecentSpamCount,
                reputation.RecentMessageCount,
                reputation.RepeatedMessageCount);
            
            var contentSignals = BuildContentSignals(eventRecord.Message, normalizedMessage, reputation);
            _logger.LogInformation("[TEST] Content signals - ContainsLink: {Link}, RepeatedMessage: {Repeated}, ScamKeyword: {Scam}, BotLike: {Bot}",
                contentSignals.ContainsLink,
                contentSignals.RepeatedMessage,
                contentSignals.ScamKeyword,
                contentSignals.BotLike);

            if (reputation.IsBlacklisted)
            {
                _logger.LogInformation("Sender {SenderId} is blacklisted. Ignoring message.", eventRecord.SenderId);
                eventRecord.State = "Processed";
                eventRecord.ActionTaken = "Ignored (Blacklisted)";
                if (eventType.Equals("comment", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(postId))
                {
                    await UpsertCommentRecordAsync(dbContext, eventRecord, postId, "processed", cancellationToken);
                }
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }

            var analysis = await _aiAnalysisService.AnalyzeTextAsync(eventRecord.Message, cancellationToken)
                ?? BuildFallbackAnalysis(eventRecord.Message, contentSignals);

            NormalizeAnalysis(analysis);
            await AddEventStatusAsync(dbContext, eventRecord, "Analyzed", "AI analysis completed");
            _logger.LogInformation("[TEST] AI Analysis - IsSpam: {IsSpam}, Intent: {Intent}, Sentiment: {Sentiment}, RiskLevel: {Risk}, RequiresReview: {RequiresReview}",
                analysis.IsSpam,
                analysis.Intent,
                analysis.Sentiment,
                analysis.RiskLevel,
                analysis.RequiresManualReview);
            
            eventRecord.IsSpam = analysis.IsSpam || contentSignals.IsSpam;
            eventRecord.SpamReason = BuildSpamReason(analysis, contentSignals);
            eventRecord.Intent = analysis.Intent;
            eventRecord.Sentiment = analysis.Sentiment;

            if (eventType.Equals("comment", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(postId))
            {
                await UpsertCommentRecordAsync(dbContext, eventRecord, postId, "processed", cancellationToken, analysis.Intent, analysis.Sentiment);
            }

            var decision = DecideModeration(analysis, contentSignals, reputation);
            _logger.LogInformation("[TEST] Moderation decision - ShouldHide: {Hide}, RequiresBlacklist: {Blacklist}, RequiresManualReview: {Review}, SuppressAutoReply: {Suppress}, RiskLevel: {Risk}, Reasons: {Reasons}",
                decision.ShouldHideComment,
                decision.RequiresBlacklist,
                decision.RequiresManualReview,
                decision.SuppressAutoReply,
                decision.RiskLevel,
                string.Join(", ", decision.Reasons));

            if (decision.RequiresBlacklist)
            {
                await UpsertBlacklistEntryAsync(dbContext, eventRecord, decision, reputation, cancellationToken);
                await AddEventStatusAsync(dbContext, eventRecord, "Blacklisted", "Added to internal blacklist");
            }

            if (decision.RequiresManualReview)
            {
                await QueueManualReviewAsync(dbContext, eventRecord, decision, cancellationToken);
                await AddEventStatusAsync(dbContext, eventRecord, "QueuedForReview", "Queued for manual moderation");
            }

            var replyCommands = new List<FacebookCommandMessage>();

            if (eventType.Equals("comment", StringComparison.OrdinalIgnoreCase))
            {
                // Spam rule: hide comment (ẩn message/comment) when analysis says shouldHide
                if (decision.ShouldHideComment)
                {
                    replyCommands.Add(BuildHideCommentCommand(eventRecord, decision));
                    eventRecord.ActionTaken = AppendAction(eventRecord.ActionTaken, "Hide Command Queued");
                }
                else
                {
                    _logger.LogInformation("[TEST] No hide action - ShouldHide=false");
                    eventRecord.ActionTaken = decision.ActionTaken;
                }
            }
            else
            {
                // Only comment type supports hide_comment in this demo.
                // For other event types (inbox message) we keep only auto-reply / suppress behavior.
                if (decision.ShouldHideComment)
                {
                    _logger.LogInformation("[TEST] Hide skipped - EventId: {EventId}, EventType: {Type} (not a comment)", eventRecord.EventId, eventType);
                    eventRecord.ActionTaken = AppendAction(eventRecord.ActionTaken, $"Hide Skipped ({eventType})");
                }
                else
                {
                    eventRecord.ActionTaken = decision.ActionTaken;
                }
            }

            // Luật tự động hóa yêu cầu:
            // - Tích cực => cảm ơn
            // - Tiêu cực => xin lỗi
            // - Spam => ẩn message/comment (không reply)
            // Hide requirement:
            // - nếu spam hoặc shouldHideComment thì chỉ hide (không reply)
            if (!decision.SuppressAutoReply && !eventRecord.IsSpam && eventType.Equals("comment", StringComparison.OrdinalIgnoreCase))
            {
                var autoReply = _autoReplyService.GetReplyForSentiment(analysis.Sentiment);
                if (!string.IsNullOrWhiteSpace(autoReply))
                {
                    replyCommands.Add(BuildReplyCommentCommand(eventRecord, decision, autoReply));
                    var label = analysis.Sentiment == "negative" ? "Apologized Reply Queued" : "Thank You Reply Queued";
                    eventRecord.ActionTaken = AppendAction(eventRecord.ActionTaken, label);
                }
            }


            // Inbox message automation (EventType="message") -> reply via Graph messaging
            if (!decision.SuppressAutoReply && eventType.Equals("message", StringComparison.OrdinalIgnoreCase))
            {
                // Với messaging event, NormalizedEvent định nghĩa:
                // - ActorId = senderId (người gửi nhắn)
                // - ObjectId = recipientId (người nhận/PSID của page)
                // Backend reply_message dùng TargetId = recipientId (người nhận).

// spam/high-risk: policy currently suppresses auto reply via decision.SuppressAutoReply
                var autoReply = _autoReplyService.GetReplyForSentiment(analysis.Sentiment);
                if (!string.IsNullOrWhiteSpace(autoReply))
                {
                    // NormalizedEvent mapping (webhook-service/FacebookPayloadNormalizer):
                    // - message.sender.id     = ActorId (người gửi)
                    // - message.recipient.id  = ObjectId (người nhận/page)
                    // Với action reply_message, Graph cần recipient là PSID của người NHẬN trong tin nhắn.

                    // Graph messaging (reply via /{recipient}/messages) cần PSID của người nhận tin nhắn.
                    // Trong payload webhook-service: message.sender.id là người gửi tin nhắn.
                    // message.recipient.id thường là Page/recipient liên quan. Để bot trả lời đúng người gửi,
                    // TargetId phải là ActorId(sender PSID).
                    var targetPsid = eventRecord.SenderId;

                    _logger.LogInformation("[DEBUG] Inbox reply_message: SourceEventId={EventId}, ActorId(SenderId)={SenderId}, ObjectId(RecipientId)={RecipientId}, TargetId={TargetId}, AutoReply='{AutoReply}'",
                        eventRecord.EventId,
                        eventRecord.SenderId,
                        eventRecord.RecipientId,
                        targetPsid,
                        autoReply);

                    var replyCommand = new FacebookCommandMessage
                    {
                        SourceEventId = eventRecord.EventId,
                        Action = "reply_message",
                        TargetId = targetPsid,
                        ActorId = eventRecord.SenderId,
                        Message = autoReply,
                        Reason = string.Join(", ", decision.Reasons),
                        Topic = KafkaTopics.ReplyCommands
                    };

                    replyCommands.Add(replyCommand);


                    var label = analysis.Sentiment == "negative" ? "Inbox Apology Reply Queued" : "Inbox Thank You Reply Queued";
                    eventRecord.ActionTaken = AppendAction(eventRecord.ActionTaken, label);
                }
            }


            if (replyCommands.Count > 0)
            {
                await _replyCommandPublisher.PublishAsync(replyCommands, cancellationToken);
                await AddEventStatusAsync(dbContext, eventRecord, "CommandsPublished", $"Published {replyCommands.Count} command(s) to reply_commands");
            }

            if (decision.RequiresBlacklist)
            {
                eventRecord.ActionTaken = AppendAction(eventRecord.ActionTaken, "Internal Blacklist Updated");
            }

            if (decision.RequiresManualReview)
            {
                eventRecord.ActionTaken = AppendAction(eventRecord.ActionTaken, "Queued For Manual Review");
            }

            if (decision.SuppressAutoReply)
            {
                eventRecord.ActionTaken = AppendAction(eventRecord.ActionTaken, "Auto Reply Disabled");
            }

            _logger.LogInformation(
                "Message analyzed. Intent: {Intent}, Sentiment: {Sentiment}, Risk: {RiskLevel}, Spam: {IsSpam}",
                eventRecord.Intent,
                eventRecord.Sentiment,
                decision.RiskLevel,
                eventRecord.IsSpam);

            eventRecord.State = "Processed";
            eventRecord.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            await AddEventStatusAsync(dbContext, eventRecord, "Processed", "Processing completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing event {EventId}", eventRecord.EventId);
            eventRecord.State = "Failed";
            if (eventType.Equals("comment", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(postId))
            {
                try
                {
                    await UpsertCommentRecordAsync(dbContext, eventRecord, postId, "failed", CancellationToken.None, eventRecord.Intent, eventRecord.Sentiment);
                }
                catch { /* best-effort */ }
            }
            eventRecord.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None); // Ensure state saves even if cancelled
            try
            {
                await AddEventStatusAsync(dbContext, eventRecord, "Failed", ex.Message);
            }
            catch { /* best-effort */ }
            throw; // Let worker handle exception
        }
    }

        private static FacebookCommandMessage BuildHideCommentCommand(EventRecord eventRecord, ModerationDecision decision)
        {
            return new FacebookCommandMessage
            {
                SourceEventId = eventRecord.EventId,
                Action = "hide_comment",
                TargetId = eventRecord.EventId,
                ActorId = eventRecord.SenderId,
                Reason = string.Join(", ", decision.Reasons),
                Topic = KafkaTopics.ReplyCommands
            };
        }

        private static FacebookCommandMessage BuildReplyCommentCommand(EventRecord eventRecord, ModerationDecision decision, string replyText)
        {
            return new FacebookCommandMessage
            {
                SourceEventId = eventRecord.EventId,
                Action = "reply_comment",
                TargetId = eventRecord.EventId,
                Message = replyText,
                ActorId = eventRecord.SenderId,
                Reason = string.Join(", ", decision.Reasons),
                Topic = KafkaTopics.ReplyCommands
            };
        }

        private static FacebookCommandMessage BuildReplyMessageCommand(EventRecord eventRecord, ModerationDecision decision, string replyText)
        {
            // For inbox automation we reply to ActorId (người gửi tin nhắn)
            // NormalizedEvent đã set ActorId = senderId, nên eventRecord.SenderId chính là recipientId cần gửi message.
            return new FacebookCommandMessage
            {
                SourceEventId = eventRecord.EventId,
                Action = "reply_message",
                TargetId = eventRecord.SenderId, // ActorId (người gửi)
                Message = replyText,
                ActorId = eventRecord.SenderId,
                Reason = string.Join(", ", decision.Reasons),
                Topic = KafkaTopics.ReplyCommands
            };
        }

    private static string? GetStringProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        return null;
    }

    private async Task AddEventStatusAsync(AppDbContext dbContext, EventRecord eventRecord, string toState, string? note = null)
    {
        try
        {
            var status = new EventProcessingStatus
            {
                EventRecordId = eventRecord.Id,
                FromState = eventRecord.State,
                ToState = toState,
                Note = note,
                CreatedAt = DateTime.UtcNow
            };

            dbContext.EventProcessingStatuses.Add(status);
            eventRecord.State = toState;
            eventRecord.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write event status for {EventId} -> {State}", eventRecord.EventId, toState);
        }
    }

    private async Task UpsertCommentRecordAsync(
        AppDbContext dbContext,
        EventRecord eventRecord,
        string postId,
        string status,
        CancellationToken cancellationToken,
        string? analysisIntent = null,
        string? analysisSentiment = null)
    {
        var comment = await dbContext.CommentRecords.FirstOrDefaultAsync(x => x.CommentId == eventRecord.EventId, cancellationToken);

        if (comment == null)
        {
            comment = new CommentRecord
            {
                CommentId = eventRecord.EventId,
                PostId = postId,
                Message = eventRecord.Message,
                Intent = analysisIntent,
                Sentiment = analysisSentiment,
                Status = status,
                CreatedAt = DateTime.UtcNow
            };

            dbContext.CommentRecords.Add(comment);
        }
        else
        {
            comment.PostId = postId;
            comment.Message = eventRecord.Message;
            comment.Intent = analysisIntent ?? comment.Intent;
            comment.Sentiment = analysisSentiment ?? comment.Sentiment;
            comment.Status = status;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<SenderReputationSnapshot> GetSenderReputationAsync(
        AppDbContext dbContext,
        string senderId,
        string normalizedMessage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(senderId))
        {
            return SenderReputationSnapshot.Empty;
        }

        var windowHours = Math.Max(1, _moderationOptions.RepeatWindowHours);
        var windowStart = DateTime.UtcNow.AddHours(-windowHours);

        var blacklist = await dbContext.SenderBlacklistEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SenderId == senderId && x.IsActive, cancellationToken);

        var recentEvents = await dbContext.EventRecords
            .AsNoTracking()
            .Where(x => x.SenderId == senderId && x.CreatedAt >= windowStart)
            .Select(x => new { x.Message, x.IsSpam })
            .ToListAsync(cancellationToken);

        var repeatedMessageCount = recentEvents.Count(x => NormalizeContent(x.Message) == normalizedMessage);
        var spamCount = recentEvents.Count(x => x.IsSpam);

        return new SenderReputationSnapshot(
            blacklist != null,
            spamCount,
            recentEvents.Count,
            repeatedMessageCount,
            blacklist);
    }

    private ContentSignals BuildContentSignals(string message, string normalizedMessage, SenderReputationSnapshot reputation)
    {
        var containsLink = HasLink(message);
        var repeatedMessage = reputation.RepeatedMessageCount + 1 >= _moderationOptions.RepeatSpamThreshold;
        var scamKeyword = ContainsKeyword(message, HighRiskKeywords);
        var botLike = IsBotLike(message, normalizedMessage);

        return new ContentSignals(
            containsLink,
            repeatedMessage,
            scamKeyword,
            botLike,
            normalizedMessage,
            reputation.RecentSpamCount,
            reputation.RecentMessageCount);
    }

    private ModerationDecision DecideModeration(AiAnalysisResult analysis, ContentSignals signals, SenderReputationSnapshot reputation)
    {
        var currentSpamCount = reputation.RecentSpamCount + ((analysis.IsSpam || signals.IsSpam) ? 1 : 0);
        var repeatSpamHit = signals.RepeatedMessage || currentSpamCount >= _moderationOptions.RepeatSpamThreshold;
        var highRisk = signals.ScamKeyword || signals.BotLike || analysis.RiskLevel == "high" || analysis.RequiresManualReview;
        var shouldHide = analysis.ShouldHide || analysis.IsSpam || signals.ContainsLink || repeatSpamHit || highRisk;
        var requiresManualReview = highRisk || (signals.ContainsLink && signals.ScamKeyword);
        var requiresBlacklist = repeatSpamHit && (analysis.IsSpam || signals.IsSpam);
        var suppressAutoReply = requiresBlacklist || shouldHide || requiresManualReview;

        var reasons = new List<string>();
        if (analysis.IsSpam || signals.ContainsLink || signals.RepeatedMessage)
        {
            reasons.Add("spam_detected");
        }

        if (signals.ScamKeyword)
        {
            reasons.Add("scam_keyword");
        }

        if (signals.BotLike)
        {
            reasons.Add("bot_like");
        }

        if (reputation.RecentSpamCount >= _moderationOptions.RepeatSpamThreshold)
        {
            reasons.Add("repeat_spam_24h");
        }

        if (reasons.Count == 0 && analysis.RequiresManualReview)
        {
            reasons.Add("ai_manual_review");
        }

        var actionTaken = requiresBlacklist
            ? "Blacklisted Internally"
            : requiresManualReview
                ? "Queued For Manual Review"
                : shouldHide
                    ? "Hidden"
                    : "No Action";

        return new ModerationDecision(
            actionTaken,
            shouldHide,
            requiresManualReview,
            requiresBlacklist,
            suppressAutoReply,
            analysis.RiskLevel ?? (highRisk ? "high" : "low"),
            reasons);
    }

    private async Task UpsertBlacklistEntryAsync(
        AppDbContext dbContext,
        EventRecord eventRecord,
        ModerationDecision decision,
        SenderReputationSnapshot reputation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(eventRecord.SenderId))
        {
            return;
        }

        var entry = reputation.BlacklistEntry;
        if (entry == null)
        {
            _logger.LogInformation("[TEST] Creating new blacklist entry for SenderId: {SenderId}", eventRecord.SenderId);
            entry = new SenderBlacklistEntry
            {
                SenderId = eventRecord.SenderId,
                FirstSeenAt = DateTime.UtcNow,
                IsActive = true
            };

            dbContext.SenderBlacklistEntries.Add(entry);
        }
        else
        {
            _logger.LogInformation("[TEST] Updating existing blacklist entry for SenderId: {SenderId}", eventRecord.SenderId);
        }

        entry.Reason = string.Join("; ", decision.Reasons.Distinct(StringComparer.OrdinalIgnoreCase));
        entry.RepeatedSpamCount = Math.Max(entry.RepeatedSpamCount, reputation.RecentSpamCount + 1);
        entry.LastSeenAt = DateTime.UtcNow;
        entry.BlacklistedAt ??= DateTime.UtcNow;
        entry.IsActive = true;

        _logger.LogInformation("[TEST] Blacklist entry finalized - SenderId: {SenderId}, RepeatedSpamCount: {Count}, Reason: {Reason}",
            eventRecord.SenderId,
            entry.RepeatedSpamCount,
            entry.Reason);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task QueueManualReviewAsync(
        AppDbContext dbContext,
        EventRecord eventRecord,
        ModerationDecision decision,
        CancellationToken cancellationToken)
    {
        var reason = string.Join("; ", decision.Reasons.Distinct(StringComparer.OrdinalIgnoreCase));
        _logger.LogInformation("[TEST] Queueing message for manual review - EventId: {EventId}, SenderId: {SenderId}, RiskLevel: {Risk}, Reason: {Reason}",
            eventRecord.EventId,
            eventRecord.SenderId,
            decision.RiskLevel,
            reason);

        var item = await dbContext.ModerationReviewItems.FirstOrDefaultAsync(x => x.EventId == eventRecord.EventId, cancellationToken);
        if (item == null)
        {
            item = new ModerationReviewItem
            {
                EventId = eventRecord.EventId,
                SenderId = eventRecord.SenderId,
                Message = eventRecord.Message,
                Reason = reason,
                RiskLevel = decision.RiskLevel,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            dbContext.ModerationReviewItems.Add(item);
        }
        else
        {
            item.SenderId = eventRecord.SenderId;
            item.Message = eventRecord.Message;
            item.Reason = reason;
            item.RiskLevel = decision.RiskLevel;
            item.Status = "Pending";
            item.UpdatedAt = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[TEST] Manual review item saved to database");
    }

    private static void NormalizeAnalysis(AiAnalysisResult analysis)
    {
        analysis.Intent = string.IsNullOrWhiteSpace(analysis.Intent) ? "khác" : analysis.Intent.Trim();
        analysis.Sentiment = NormalizeSentiment(analysis.Sentiment);
        analysis.RiskLevel = NormalizeRiskLevel(analysis.RiskLevel);
        analysis.SpamReason = string.IsNullOrWhiteSpace(analysis.SpamReason) ? null : analysis.SpamReason.Trim();
    }

    private static string BuildSpamReason(AiAnalysisResult analysis, ContentSignals signals)
    {
        var reasons = new List<string>();

        if (!string.IsNullOrWhiteSpace(analysis.SpamReason))
        {
            reasons.Add(analysis.SpamReason!);
        }

        if (signals.ContainsLink)
        {
            reasons.Add("contains_link");
        }

        if (signals.RepeatedMessage)
        {
            reasons.Add("repeated_content_24h");
        }

        if (signals.ScamKeyword)
        {
            reasons.Add("scam_keyword");
        }

        if (signals.BotLike)
        {
            reasons.Add("bot_like");
        }

        return reasons.Count == 0 ? string.Empty : string.Join(", ", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static AiAnalysisResult BuildFallbackAnalysis(string message, ContentSignals signals)
    {
        var lowered = message.ToLowerInvariant();
        var spam = signals.ContainsLink || signals.RepeatedMessage || signals.ScamKeyword || signals.BotLike;

        return new AiAnalysisResult
        {
            IsSpam = spam,
            SpamReason = spam ? BuildFallbackSpamReason(signals) : null,
            Intent = DetectIntent(lowered),
            Sentiment = DetectSentiment(lowered),
            RiskLevel = signals.ScamKeyword || signals.BotLike ? "high" : (spam ? "medium" : "low"),
            RequiresManualReview = signals.ScamKeyword || signals.BotLike,
            ShouldHide = spam
        };
    }

    private static string BuildFallbackSpamReason(ContentSignals signals)
    {
        var reasons = new List<string>();

        if (signals.ContainsLink)
        {
            reasons.Add("contains_link");
        }

        if (signals.RepeatedMessage)
        {
            reasons.Add("repeated_content_24h");
        }

        if (signals.ScamKeyword)
        {
            reasons.Add("scam_keyword");
        }

        if (signals.BotLike)
        {
            reasons.Add("bot_like");
        }

        return string.Join(", ", reasons);
    }

    private static string DetectIntent(string lowered)
    {
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

    private static string DetectSentiment(string lowered)
    {
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

    private async Task<bool> HasEventStatusAsync(AppDbContext dbContext, string eventRecordId, string toState, CancellationToken cancellationToken)
    {
        return await dbContext.EventProcessingStatuses
            .AsNoTracking()
            .AnyAsync(x => x.EventRecordId == eventRecordId && x.ToState == toState, cancellationToken);
    }

    private static string? BuildAutoReplyMessage(string? sentiment)
    {
        return sentiment?.Trim().ToLowerInvariant() switch
        {
            "positive" => "Cảm ơn bạn đã ủng hộ shop!",
            "negative" => "Rất xin lỗi vì trải nghiệm chưa tốt, bên mình sẽ kiểm tra ngay.",
            _ => null
        };
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

    private static string NormalizeContent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value
            .Trim()
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool HasLink(string message)
    {
        return message.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("www.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsKeyword(string message, IEnumerable<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBotLike(string message, string normalizedMessage)
    {
        if (HasLink(message) && normalizedMessage.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3)
        {
            return true;
        }

        return message.Contains("!!!!", StringComparison.Ordinal) ||
               message.Contains("????", StringComparison.Ordinal) ||
               message.Contains("$$$$", StringComparison.Ordinal);
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

    private static string AppendAction(string? existingAction, string newAction)
    {
        return string.IsNullOrWhiteSpace(existingAction)
            ? newAction
            : $"{existingAction} | {newAction}";
    }
}
