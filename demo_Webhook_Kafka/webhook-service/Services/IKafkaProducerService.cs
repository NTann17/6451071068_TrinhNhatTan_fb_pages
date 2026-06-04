using webhook_service.Models;

namespace webhook_service.Services;

public interface IKafkaProducerService
{
    Task PublishBatchAsync(IEnumerable<NormalizedEvent> events, CancellationToken cancellationToken);
}