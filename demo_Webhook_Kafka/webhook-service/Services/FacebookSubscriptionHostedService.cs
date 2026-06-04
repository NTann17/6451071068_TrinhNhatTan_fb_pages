using Microsoft.Extensions.Hosting;

namespace webhook_service.Services;

public sealed class FacebookSubscriptionHostedService : IHostedService
{
    private readonly IFacebookGraphApiClient _graphApiClient;
    private readonly ILogger<FacebookSubscriptionHostedService> _logger;

    public FacebookSubscriptionHostedService(
        IFacebookGraphApiClient graphApiClient,
        ILogger<FacebookSubscriptionHostedService> logger)
    {
        _graphApiClient = graphApiClient;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _graphApiClient.SubscribePageAsync(null, cancellationToken);
            _logger.LogInformation(
                "Facebook page subscription ensured on startup. Fields={Fields}, Success={Success}",
                result.SubscribedFields,
                result.Success);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Skipping Facebook auto-subscription because configuration is incomplete");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Facebook auto-subscription failed on startup");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}