using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace backend_api.Auth;

public sealed class DashboardApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DashboardApiKey";
    public const string HeaderName = "X-Admin-Api-Key";

    private readonly IConfiguration _configuration;

    public DashboardApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expectedApiKey = _configuration["AdminAuth:ApiKey"];
        if (string.IsNullOrWhiteSpace(expectedApiKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Admin API key is not configured."));
        }

        if (!Request.Headers.TryGetValue(HeaderName, out var suppliedApiKey) || string.IsNullOrWhiteSpace(suppliedApiKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!string.Equals(suppliedApiKey.ToString(), expectedApiKey, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid admin API key."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "dashboard-admin"),
            new Claim(ClaimTypes.Name, "dashboard-admin"),
            new Claim(ClaimTypes.Role, "Admin")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}