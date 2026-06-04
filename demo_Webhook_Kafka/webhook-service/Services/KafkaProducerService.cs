using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using webhook_service.Models;

namespace webhook_service.Services;

public sealed class KafkaProducerService : IKafkaProducerService, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaProducerService> _logger;

    public KafkaProducerService(IOptions<KafkaOptions> options, ILogger<KafkaProducerService> logger)
    {
        _options = options.Value;
        _logger = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10000
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishBatchAsync(IEnumerable<NormalizedEvent> events, CancellationToken cancellationToken)
    {
        foreach (var normalizedEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = normalizedEvent.CommandId ?? normalizedEvent.EventId;
            var value = JsonSerializer.Serialize(normalizedEvent);

            try
            {
                var delivery = await _producer.ProduceAsync(
                    _options.Topic,
                    new Message<string, string>
                    {
                        Key = key,
                        Value = value
                    },
                    cancellationToken);

                _logger.LogInformation(
                    "Published event {EventId} to {Topic} partition {Partition} offset {Offset}",
                    normalizedEvent.EventId,
                    delivery.Topic,
                    delivery.Partition,
                    delivery.Offset);
            }
            catch (ProduceException<string, string> ex)
            {
                _logger.LogError(ex, "Kafka publish failed for event {EventId}", normalizedEvent.EventId);
                throw;
            }
        }

        _producer.Flush(cancellationToken);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}