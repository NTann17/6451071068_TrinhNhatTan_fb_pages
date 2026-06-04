using System.Text.Json;
using System.Threading.Channels;
using Confluent.Kafka;
using core_service.Services;
using Microsoft.Extensions.Primitives;

namespace core_service.Workers;

public class EventConsumerWorker : BackgroundService
{
    private readonly ILogger<EventConsumerWorker> _logger;
    private readonly EventProcessingPipeline _pipeline;
    private readonly IConfiguration _configuration;

    public EventConsumerWorker(
        ILogger<EventConsumerWorker> logger, 
        EventProcessingPipeline pipeline,
        IConfiguration configuration)
    {
        _logger = logger;
        _pipeline = pipeline;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var bootstrapServers = _configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092";
        var topic = _configuration["KAFKA_TOPIC"] ?? "raw_events";
        var groupId = _configuration["KAFKA_GROUP_ID"] ?? "core-service-group";

        var bufferCapacity = int.TryParse(_configuration["KAFKA_CONSUMER_BUFFER_CAPACITY"], out var cap) ? cap : 1000;
        var workerCount = int.TryParse(_configuration["KAFKA_CONSUMER_WORKERS"], out var wc) ? Math.Max(1, wc) : Math.Max(4, Environment.ProcessorCount);
        var maxRetries = int.TryParse(_configuration["KAFKA_CONSUMER_RETRY_COUNT"], out var mr) ? Math.Max(0, mr) : 3;
        var dlqTopic = _configuration["KAFKA_DLQ_TOPIC"] ?? "dead_letter";

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = true
        };

        using var consumer = new ConsumerBuilder<Ignore, string>(config).Build();
        consumer.Subscribe(topic);

        _logger.LogInformation("Kafka Consumer started. Subscribed to topic {Topic}. BufferCapacity={Buffer}, Workers={Workers}", topic, bufferCapacity, workerCount);

        // bounded channel provides backpressure when load spikes
        var channelOptions = new BoundedChannelOptions(bufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        };

        var inbound = Channel.CreateBounded<ConsumeResult<Ignore, string>>(channelOptions);
        var processed = Channel.CreateUnbounded<TopicPartitionOffset>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // Start worker pool
        var workerTasks = new List<Task>();
        for (int i = 0; i < workerCount; i++)
        {
            workerTasks.Add(Task.Run(() => WorkerLoop(inbound.Reader, processed.Writer, maxRetries, dlqTopic, stoppingToken), stoppingToken));
        }

        // Commit loop runs on this thread to safely access consumer for commits
        var commitTask = Task.Run(() => CommitLoop(consumer, processed.Reader, stoppingToken), stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var consumeResult = consumer.Consume(stoppingToken);
                    if (consumeResult?.Message == null) continue;

                    // Enqueue for processing (will wait if buffer full -> backpressure)
                    await inbound.Writer.WriteAsync(consumeResult, stoppingToken);

                    // Periodically log queue length
                    if (inbound.Reader.Count % 500 == 0)
                    {
                        _logger.LogInformation("Inbound buffer length: {Count}", inbound.Reader.Count);
                    }
                }
                catch (ConsumeException e)
                {
                    _logger.LogError(e, "Error consuming from Kafka");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in consumer loop");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Kafka Consumer is stopping.");
        }
        finally
        {
            inbound.Writer.Complete();
            await Task.WhenAll(workerTasks.Concat(new[] { commitTask }));

            // Commit last processed offsets if any
            try
            {
                consumer.Commit();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to commit offsets on shutdown");
            }
            consumer.Close();
        }
    }

    private async Task WorkerLoop(ChannelReader<ConsumeResult<Ignore, string>> reader, ChannelWriter<TopicPartitionOffset> processedWriter, int maxRetries, string dlqTopic, CancellationToken stoppingToken)
    {
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = _configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092"
        };

        using var producer = new ProducerBuilder<Null, string>(producerConfig).Build();

        await foreach (var consumeResult in reader.ReadAllAsync(stoppingToken))
        {
            var tpo = consumeResult.TopicPartitionOffset;
            var messageValue = consumeResult.Message.Value;

            var success = false;
            for (var attempt = 1; attempt <= maxRetries + 1 && !stoppingToken.IsCancellationRequested; attempt++)
            {
                try
                {
                    await _pipeline.ProcessEventAsync(messageValue, stoppingToken);
                    success = true;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Processing failed for {TopicPartitionOffset} attempt {Attempt}/{MaxAttempts}", tpo, attempt, maxRetries + 1);
                    // exponential backoff
                    if (attempt <= maxRetries)
                    {
                        var backoffDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                        await Task.Delay(backoffDelay, stoppingToken);
                    }
                }
            }

            if (!success)
            {
                _logger.LogError("Message processing permanently failed for {TopicPartitionOffset}. Sending to DLQ: {DlqTopic}", tpo, dlqTopic);
                try
                {
                    var deadLetterPayload = BuildDeadLetterPayload(messageValue, tpo, maxRetries + 1);
                    await producer.ProduceAsync(dlqTopic, new Message<Null, string> { Value = deadLetterPayload }, stoppingToken);
                    _logger.LogError("DLQ published for {TopicPartitionOffset}. Alert marker: DEAD_LETTER_PUBLISHED", tpo);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to produce message to DLQ {DlqTopic}", dlqTopic);
                }
            }

            // Notify commit loop that this offset has been processed (success or failed)
            try
            {
                await processedWriter.WriteAsync(tpo, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue processed offset {Tpo}", tpo);
            }
        }
    }

    private async Task CommitLoop(IConsumer<Ignore, string> consumer, ChannelReader<TopicPartitionOffset> processedReader, CancellationToken stoppingToken)
    {
        var highestByPartition = new Dictionary<TopicPartition, long>();

        await foreach (var tpo in processedReader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var tp = tpo.TopicPartition;
                var offsetValue = tpo.Offset.Value; // processed message offset

                if (!highestByPartition.TryGetValue(tp, out var curr) || offsetValue > curr)
                {
                    highestByPartition[tp] = offsetValue;
                }

                // compute commit offset as highest + 1
                var commitList = highestByPartition.Select(kvp => new TopicPartitionOffset(kvp.Key, new Offset(kvp.Value + 1))).ToList();
                if (commitList.Count > 0)
                {
                    try
                    {
                        consumer.Commit(commitList);
                        _logger.LogDebug("Committed offsets: {Offsets}", string.Join(',', commitList.Select(x => x.ToString())));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to commit offsets");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in commit loop for offset {Tpo}", tpo);
            }
        }
    }

    private static string BuildDeadLetterPayload(string originalMessage, TopicPartitionOffset tpo, int attempts)
    {
        string? commandId = null;
        string? eventType = null;

        try
        {
            using var document = JsonDocument.Parse(originalMessage);
            var root = document.RootElement;
            commandId = GetStringProperty(root, "CommandId", "commandId", "EventId", "eventId", "Id", "id");
            eventType = GetStringProperty(root, "EventType", "eventType");
        }
        catch
        {
            // keep the original payload if parsing fails
        }

        var envelope = new
        {
            DeadLetteredAt = DateTime.UtcNow,
            CommandId = commandId,
            EventType = eventType,
            Attempts = attempts,
            Topic = tpo.Topic,
            Partition = tpo.Partition.Value,
            Offset = tpo.Offset.Value,
            Reason = "Processing exhausted retry attempts",
            AlertHint = "Prometheus alert should page on dead_letter topic growth",
            OriginalMessage = originalMessage
        };

        return JsonSerializer.Serialize(envelope);
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
}
