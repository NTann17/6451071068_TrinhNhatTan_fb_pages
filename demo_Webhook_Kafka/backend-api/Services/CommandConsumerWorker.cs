using System.Text.Json;
using Confluent.Kafka;
using DemoWebhookKafka.Contracts;
using backend_api.Data;
using backend_api.Models;
using Microsoft.EntityFrameworkCore;

namespace backend_api.Services;

public sealed class CommandConsumerWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFacebookActionClient _facebookActionClient;
    private readonly ISendFailedPublisher _sendFailedPublisher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CommandConsumerWorker> _logger;

    public CommandConsumerWorker(
        IServiceScopeFactory scopeFactory,
        IFacebookActionClient facebookActionClient,
        ISendFailedPublisher sendFailedPublisher,
        IConfiguration configuration,
        ILogger<CommandConsumerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _facebookActionClient = facebookActionClient;
        _sendFailedPublisher = sendFailedPublisher;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var bootstrapServers = _configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092";
        var groupId = _configuration["KAFKA_GROUP_ID"] ?? "backend-api-group";
        var replyCommandsTopic = _configuration["KAFKA_REPLY_COMMANDS_TOPIC"] ?? KafkaTopics.ReplyCommands;
        var sendRetryTopic = _configuration["KAFKA_SEND_RETRY_TOPIC"] ?? KafkaTopics.SendRetry;

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = true
        };

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        consumer.Subscribe(new[] { replyCommandsTopic, sendRetryTopic });

        _logger.LogInformation("Backend command consumer subscribed to {ReplyTopic} and {RetryTopic}", replyCommandsTopic, sendRetryTopic);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? consumeResult = null;

            try
            {
                consumeResult = consumer.Consume(stoppingToken);
                if (consumeResult?.Message == null)
                {
                    continue;
                }

                if (!TryParseCommand(consumeResult.Message.Value, out var command))
                {
                    _logger.LogWarning("Skipping invalid Kafka payload from {TopicPartitionOffset}", consumeResult.TopicPartitionOffset);
                    consumer.Commit(consumeResult);
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BackendDbContext>();

                if (!await TryAcquireCommandAsync(db, command, stoppingToken))
                {
                    _logger.LogInformation("Command {CommandId} already processed or locked. Skipping.", command.CommandId);
                    consumer.Commit(consumeResult);
                    continue;
                }

                try
                {
                    _logger.LogInformation(
                        "[DEBUG][CommandConsumerWorker] Execute commandId={CommandId} action={Action} targetId={TargetId} actorId={ActorId} sourceEventId={SourceEventId}",
                        command.CommandId,
                        command.Action,
                        command.TargetId,
                        command.ActorId,
                        command.SourceEventId);

                    if (command.Action != null && command.Action.Trim().Equals("reply_message", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("[DEBUG][CommandConsumerWorker] reply_message body Message='{Message}'", command.Message);
                    }

                    await ExecuteFacebookCommandAsync(command, stoppingToken);
                    await MarkProcessedAsync(db, command, stoppingToken);
                    consumer.Commit(consumeResult);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "[DEBUG][CommandConsumerWorker] MarkFailed reason={Reason} commandId={CommandId} action={Action} targetId={TargetId}",
                        ex.Message,
                        command.CommandId,
                        command.Action,
                        command.TargetId);

                    await MarkFailedAsync(db, command, ex.Message, stoppingToken);
                    await _sendFailedPublisher.PublishAsync(command, ex.Message, stoppingToken);
                    consumer.Commit(consumeResult);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error in backend-api");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while processing Kafka command");
                if (consumeResult != null)
                {
                    try
                    {
                        consumer.Commit(consumeResult);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
    }

    private async Task ExecuteFacebookCommandAsync(FacebookCommandMessage command, CancellationToken cancellationToken)
    {
        var targetId = command.TargetId ?? command.SourceEventId;

        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new InvalidOperationException($"Command {command.CommandId} is missing a target id.");
        }

        switch (command.Action.Trim().ToLowerInvariant())
        {
            case "hide_comment":
                if (!await _facebookActionClient.HideCommentAsync(targetId, cancellationToken))
                {
                    throw new InvalidOperationException($"Facebook rejected hide_comment for {targetId}.");
                }
                break;

            case "reply_comment":
                if (string.IsNullOrWhiteSpace(command.Message))
                {
                    throw new InvalidOperationException($"Command {command.CommandId} is missing reply text.");
                }

                if (!await _facebookActionClient.ReplyToCommentAsync(targetId, command.Message, cancellationToken))
                {
                    throw new InvalidOperationException($"Facebook rejected reply_comment for {targetId}.");
                }
                break;

            case "reply_message":
                if (string.IsNullOrWhiteSpace(command.Message))
                {
                    throw new InvalidOperationException($"Command {command.CommandId} is missing reply text.");
                }

                if (!await _facebookActionClient.ReplyToMessageAsync(targetId, command.Message, cancellationToken))
                {
                    throw new InvalidOperationException($"Facebook rejected reply_message for {targetId}.");
                }
                break;

            default:
                throw new InvalidOperationException($"Unsupported command action '{command.Action}'.");
        }
    }

    private static bool TryParseCommand(string payload, out FacebookCommandMessage command)
    {
        try
        {
            command = JsonSerializer.Deserialize<FacebookCommandMessage>(payload, _jsonOptions) ?? new FacebookCommandMessage();
            return !string.IsNullOrWhiteSpace(command.CommandId) && !string.IsNullOrWhiteSpace(command.Action);
        }
        catch
        {
            command = new FacebookCommandMessage();
            return false;
        }
    }

    private static async Task<bool> TryAcquireCommandAsync(BackendDbContext db, FacebookCommandMessage command, CancellationToken cancellationToken)
    {
        var existing = await db.IdempotencyKeys.AsTracking().FirstOrDefaultAsync(x => x.CommandId == command.CommandId, cancellationToken);

        if (existing is null)
        {
            db.IdempotencyKeys.Add(new IdempotencyKey
            {
                CommandId = command.CommandId,
                Status = "Processing",
                ProcessedAt = null
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                return await TryAcquireCommandAsync(db, command, cancellationToken);
            }
        }

        if (string.Equals(existing.Status, "Processed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(existing.Status, "Processing", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        existing.Status = "Processing";
        existing.ProcessedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task MarkProcessedAsync(BackendDbContext db, FacebookCommandMessage command, CancellationToken cancellationToken)
    {
        var existing = await db.IdempotencyKeys.FirstAsync(x => x.CommandId == command.CommandId, cancellationToken);
        existing.Status = "Processed";
        existing.ProcessedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task MarkFailedAsync(BackendDbContext db, FacebookCommandMessage command, string reason, CancellationToken cancellationToken)
    {
        var existing = await db.IdempotencyKeys.FirstAsync(x => x.CommandId == command.CommandId, cancellationToken);
        existing.Status = "Failed";
        existing.ProcessedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}

