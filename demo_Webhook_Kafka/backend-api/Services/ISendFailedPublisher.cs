using DemoWebhookKafka.Contracts;

namespace backend_api.Services;

public interface ISendFailedPublisher
{
    Task PublishAsync(FacebookCommandMessage command, string reason, CancellationToken cancellationToken);
}