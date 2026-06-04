using System.Text.Json;
using Confluent.Kafka;
using DemoWebhookKafka.Contracts;

namespace retry_service.Services;

public sealed class RetryPublisher : IRetryPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<RetryPublisher> _logger;

    public RetryPublisher(IConfiguration configuration, ILogger<RetryPublisher> logger)
    {
        _logger = logger;
        var bootstrap = configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092";

        var config = new ProducerConfig
        {
            BootstrapServers = bootstrap,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10000
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync(string topic, FacebookCommandMessage message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await _producer.ProduceAsync(
            topic,
            new Message<string, string>
            {
                Key = message.CommandId,
                Value = json
            },
            cancellationToken);

        _logger.LogInformation("Published command {CommandId} to {Topic} with retry {RetryCount}", message.CommandId, topic, message.RetryCount);
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