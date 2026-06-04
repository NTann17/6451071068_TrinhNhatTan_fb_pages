using DemoWebhookKafka.Contracts;

namespace retry_service.Services;

public interface IRetryPublisher
{
    Task PublishAsync(string topic, FacebookCommandMessage message, CancellationToken cancellationToken);
}