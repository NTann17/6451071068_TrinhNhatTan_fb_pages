namespace webhook_service.Models;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9094";

    public string Topic { get; set; } = "raw_events";
}