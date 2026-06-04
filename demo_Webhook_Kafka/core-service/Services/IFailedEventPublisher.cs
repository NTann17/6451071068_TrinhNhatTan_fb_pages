using core_service.Models;

namespace core_service.Services;

public interface IFailedEventPublisher
{
    Task PublishSendFailedAsync(EventRecord eventRecord, string reason, CancellationToken cancellationToken);
}
