using System.Text.Json;
using Confluent.Kafka;
using DemoWebhookKafka.Contracts;

namespace retry_service.Services;

public sealed class RetryWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRetryPublisher _retryPublisher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RetryWorker> _logger;

    public RetryWorker(IRetryPublisher retryPublisher, IConfiguration configuration, ILogger<RetryWorker> logger)
    {
        _retryPublisher = retryPublisher;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var bootstrapServers = _configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092";
        var groupId = _configuration["KAFKA_GROUP_ID"] ?? "retry-service-group";
        var sendFailedTopic = _configuration["KAFKA_SEND_FAILED_TOPIC"] ?? KafkaTopics.SendFailed;
        var sendRetryTopic = _configuration["KAFKA_SEND_RETRY_TOPIC"] ?? KafkaTopics.SendRetry;
        var deadLetterTopic = _configuration["KAFKA_DLQ_TOPIC"] ?? KafkaTopics.DeadLetter;
        var maxRetries = int.TryParse(_configuration["KAFKA_MAX_RETRIES"], out var parsedMaxRetries) ? Math.Max(0, parsedMaxRetries) : 5;

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
        consumer.Subscribe(sendFailedTopic);

        _logger.LogInformation("Retry worker subscribed to {SendFailedTopic}", sendFailedTopic);

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
                    _logger.LogWarning("Skipping invalid retry payload from {TopicPartitionOffset}", consumeResult.TopicPartitionOffset);
                    consumer.Commit(consumeResult);
                    continue;
                }

                var delaySeconds = Math.Max(1, (int)Math.Pow(2, command.RetryCount));
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);

                if (command.RetryCount >= maxRetries)
                {
                    var deadLetterMessage = command with
                    {
                        Topic = KafkaTopics.DeadLetter,
                        LastAttemptAt = DateTimeOffset.UtcNow,
                        FailureReason = command.FailureReason ?? "Exceeded retry threshold"
                    };

                    await _retryPublisher.PublishAsync(deadLetterTopic, deadLetterMessage, stoppingToken);
                    _logger.LogWarning("Command {CommandId} moved to dead_letter after {RetryCount} retries", command.CommandId, command.RetryCount);
                    consumer.Commit(consumeResult);
                    continue;
                }

                var retryMessage = command with
                {
                    RetryCount = command.RetryCount + 1,
                    Topic = KafkaTopics.SendRetry,
                    LastAttemptAt = DateTimeOffset.UtcNow
                };

                await _retryPublisher.PublishAsync(sendRetryTopic, retryMessage, stoppingToken);
                consumer.Commit(consumeResult);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error in retry-service");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while processing retry command");
                if (consumeResult != null)
                {
                    try
                    {
                        consumer.Commit(consumeResult);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private static bool TryParseCommand(string payload, out FacebookCommandMessage command)
    {
        try
        {
            command = JsonSerializer.Deserialize<FacebookCommandMessage>(payload, JsonOptions) ?? new FacebookCommandMessage();
            return !string.IsNullOrWhiteSpace(command.CommandId);
        }
        catch
        {
            command = new FacebookCommandMessage();
            return false;
        }
    }
}