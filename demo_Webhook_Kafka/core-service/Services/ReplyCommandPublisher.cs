using System.Text.Json;
using Confluent.Kafka;
using DemoWebhookKafka.Contracts;

namespace core_service.Services;

public sealed class ReplyCommandPublisher : IReplyCommandPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;
    private readonly ILogger<ReplyCommandPublisher> _logger;

    public ReplyCommandPublisher(IConfiguration configuration, ILogger<ReplyCommandPublisher> logger)
    {
        _logger = logger;
        var bootstrap = configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092";
        _topic = configuration["KAFKA_REPLY_COMMANDS_TOPIC"] ?? KafkaTopics.ReplyCommands;

        var config = new ProducerConfig
        {
            BootstrapServers = bootstrap,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10000
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync(IEnumerable<FacebookCommandMessage> commands, CancellationToken cancellationToken)
    {
        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var value = JsonSerializer.Serialize(command);
            await _producer.ProduceAsync(
                _topic,
                new Message<string, string>
                {
                    Key = command.CommandId,
                    Value = value
                },
                cancellationToken);

            _logger.LogInformation("Published reply command {CommandId} to {Topic} with action {Action}", command.CommandId, _topic, command.Action);
        }

        _producer.Flush(cancellationToken);
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