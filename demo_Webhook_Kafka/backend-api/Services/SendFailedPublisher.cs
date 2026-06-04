using System.Text.Json;
using Confluent.Kafka;
using DemoWebhookKafka.Contracts;

namespace backend_api.Services;

public sealed class SendFailedPublisher : ISendFailedPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;
    private readonly ILogger<SendFailedPublisher> _logger;

    public SendFailedPublisher(IConfiguration configuration, ILogger<SendFailedPublisher> logger)
    {
        _logger = logger;
        var bootstrap = configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092";
        _topic = configuration["KAFKA_SEND_FAILED_TOPIC"] ?? KafkaTopics.SendFailed;

        var config = new ProducerConfig
        {
            BootstrapServers = bootstrap,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10000
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync(FacebookCommandMessage command, string reason, CancellationToken cancellationToken)
    {
        var failedCommand = command with
        {
            FailureReason = reason,
            Topic = KafkaTopics.SendFailed,
            LastAttemptAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(failedCommand);
        await _producer.ProduceAsync(
            _topic,
            new Message<string, string>
            {
                Key = failedCommand.CommandId,
                Value = json
            },
            cancellationToken);

        _logger.LogInformation("Published send_failed for command {CommandId} to topic {Topic}", failedCommand.CommandId, _topic);
    }

    public void Dispose()
    {
        try
        {
            _producer.Flush(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        _producer.Dispose();
    }
}