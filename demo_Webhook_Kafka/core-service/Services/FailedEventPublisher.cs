using System.Text.Json;
using Confluent.Kafka;
using core_service.Models;

namespace core_service.Services;

public class FailedEventPublisher : IFailedEventPublisher, IDisposable
{
    private readonly IProducer<Null, string> _producer;
    private readonly string _topic;
    private readonly ILogger<FailedEventPublisher> _logger;

    public FailedEventPublisher(IConfiguration configuration, ILogger<FailedEventPublisher> logger)
    {
        _logger = logger;
        var bootstrap = configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092";
        _topic = configuration["KAFKA_SEND_FAILED_TOPIC"] ?? "send_failed";

        var config = new ProducerConfig { BootstrapServers = bootstrap };
        _producer = new ProducerBuilder<Null, string>(config).Build();
    }

    public async Task PublishSendFailedAsync(EventRecord eventRecord, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var payload = new
            {
                EventId = eventRecord.EventId,
                SenderId = eventRecord.SenderId,
                Message = eventRecord.Message,
                ActionTaken = eventRecord.ActionTaken,
                Reason = reason,
                RetryCount = 0,
                OccurredAt = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(payload);
            await _producer.ProduceAsync(_topic, new Message<Null, string> { Value = json }, cancellationToken);
            _logger.LogInformation("Published send_failed for EventId {EventId} to topic {Topic}", eventRecord.EventId, _topic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish send_failed for EventId {EventId}", eventRecord.EventId);
        }
    }

    public void Dispose()
    {
        try
        {
            _producer.Flush(TimeSpan.FromSeconds(3));
            _producer.Dispose();
        }
        catch { }
    }
}
