using System.Text.Json;
using webhook_service.Models;

namespace webhook_service.Services;

public interface IFacebookPayloadNormalizer
{
    IReadOnlyList<NormalizedEvent> Normalize(JsonElement payload);
}