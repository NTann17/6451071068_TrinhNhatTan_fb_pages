using DemoWebhookKafka.Contracts;

namespace core_service.Services;

public interface IReplyCommandPublisher
{
    Task PublishAsync(IEnumerable<FacebookCommandMessage> commands, CancellationToken cancellationToken);
}