using webhook_service.Models;
using webhook_service.Services;

using backend_api.Auth;
using backend_api.Contracts;
using backend_api.Data;
using backend_api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FacebookOptions>(builder.Configuration.GetSection(FacebookOptions.SectionName));
builder.Services.AddHttpClient<IFacebookGraphApiClient, FacebookGraphApiClient>();
builder.Services.AddHttpClient<IFacebookActionClient, FacebookActionClient>();
builder.Services.AddHostedService<FacebookSubscriptionHostedService>();
builder.Services.AddDbContext<BackendDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("BackendDatabase") ?? "Data Source=backend.db"));
builder.Services.AddSingleton<ISendFailedPublisher, SendFailedPublisher>();
builder.Services.AddHostedService<CommandConsumerWorker>();
builder.Services.AddAuthentication(DashboardApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DashboardApiKeyAuthenticationHandler>(DashboardApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy
        .AddAuthenticationSchemes(DashboardApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .RequireRole("Admin"));
});
builder.Logging.AddConsole();
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.FacebookGraphApiClient", LogLevel.Warning);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Backend API",
        Version = "v1",
        Description = "Dashboard admin proxy for Facebook Graph API"
    });

    options.AddSecurityDefinition(DashboardApiKeyAuthenticationHandler.SchemeName, new OpenApiSecurityScheme
    {
        Name = DashboardApiKeyAuthenticationHandler.HeaderName,
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Admin API key for dashboard endpoints"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference
            {
                Type = ReferenceType.SecurityScheme,
                Id = DashboardApiKeyAuthenticationHandler.SchemeName
            }
        }] = Array.Empty<string>()
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BackendDbContext>();
    db.Database.EnsureCreated();
}

app.UseAuthentication();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Backend API v1");
    options.RoutePrefix = "swagger";
});

app.MapGet("/", () => Results.Ok(new { service = "backend-api", status = "running" }))
    ;
app.MapGet("/health", () => Results.Ok(new { service = "backend-api", status = "healthy" }))
    ;

var admin = app.MapGroup("")
    .RequireAuthorization("AdminOnly");

var facebook = admin.MapGroup("/api/facebook");

facebook.MapGet("/page", async (
    IFacebookGraphApiClient graphApiClient,
    CancellationToken cancellationToken) =>
{
    try
    {
        var page = await graphApiClient.GetPageInfoAsync(cancellationToken);
        return Results.Ok(new
        {
            connected = true,
            pageId = page.Id,
            pageName = page.Name
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new
        {
            connected = false,
            error = ex.Message
        });
    }
});

facebook.MapPost("/page/subscriptions", async (
    HttpContext context, 
    IFacebookGraphApiClient graphApiClient,
    CancellationToken cancellationToken) =>
{
    try
    {
        string? fields = null;
        if (context.Request.Query.TryGetValue("fields", out var fieldsFromQuery))
        {
            fields = fieldsFromQuery.ToString();
        }

        var result = await graphApiClient.SubscribePageAsync(fields, cancellationToken);
        return Results.Ok(new
        {
            success = result.Success,
            subscribedFields = result.SubscribedFields
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new
        {
            success = false,
            error = ex.Message
        });
    }
});

facebook.MapGet("/page/subscriptions/status", async (
    IFacebookGraphApiClient graphApiClient,
    CancellationToken cancellationToken) =>
{
    try
    {
        var subscribedApps = await graphApiClient.GetSubscribedAppsAsync(cancellationToken);
        var app = subscribedApps.FirstOrDefault();
        var activeFields = app?.SubscribedFields ?? Array.Empty<string>();

        return Results.Ok(new
        {
            appCount = subscribedApps.Count,
            hasFeedSubscription = activeFields.Any(field => field.Equals("feed", StringComparison.OrdinalIgnoreCase)),
            hasMessagesSubscription = activeFields.Any(field => field.Equals("messages", StringComparison.OrdinalIgnoreCase)),
            subscribedApps
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new
        {
            success = false,
            error = ex.Message
        });
    }
});

facebook.MapGet("/posts", async (
    HttpContext context, 
    IFacebookGraphApiClient graphApiClient,
    CancellationToken cancellationToken) =>
{
    var limit = ReadIntQuery(context.Request.Query["limit"], 10, 1, 100);

    try
    {
        var posts = await graphApiClient.GetPostsAsync(limit, cancellationToken);
        return Results.Ok(new ApiResponse<object>(true, posts, "Posts fetched successfully."));
    }
    catch (FacebookApiException ex)
    {
        return MapFacebookApiError(ex);
    }
    catch (Exception ex)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Unable to fetch posts", detail: ex.Message);
    }
});

facebook.MapPost("/posts", async (
    CreatePostRequest request, 
    IFacebookGraphApiClient graphApiClient,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new ApiErrorResponse("invalid_request", "Message is required."));
    }

    try
    {
        var createdPost = await graphApiClient.CreatePostAsync(request.Message, cancellationToken);
        return Results.Ok(new ApiResponse<object>(true, createdPost, "Post created successfully."));
    }
    catch (FacebookApiException ex)
    {
        return MapFacebookApiError(ex);
    }
    catch (Exception ex)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Unable to create post", detail: ex.Message);
    }
});

facebook.MapGet("/posts/{postId}/comments", async (
    string postId,
    HttpContext context,
    IFacebookGraphApiClient graphApiClient,
    CancellationToken cancellationToken) =>
{
    var limit = ReadIntQuery(context.Request.Query["limit"], 25, 1, 100);

    try
    {
        var comments = await graphApiClient.GetCommentsAsync(postId, limit, cancellationToken);
        return Results.Ok(new ApiResponse<object>(true, comments, "Comments fetched successfully."));
    }
    catch (FacebookApiException ex)
    {
        return MapFacebookApiError(ex);
    }
    catch (Exception ex)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Unable to fetch comments", detail: ex.Message);
    }
});

static IResult MapFacebookApiError(FacebookApiException ex)
{
    var statusCode = ex.StatusCode == System.Net.HttpStatusCode.Unauthorized
        ? StatusCodes.Status401Unauthorized
        : ex.StatusCode == System.Net.HttpStatusCode.Forbidden
            ? StatusCodes.Status403Forbidden
            : ex.StatusCode == System.Net.HttpStatusCode.NotFound
                ? StatusCodes.Status404NotFound
                : ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? StatusCodes.Status429TooManyRequests
                    : (int)ex.StatusCode;

    return Results.Problem(
        statusCode: statusCode,
        title: ex.FriendlyMessage,
        detail: ex.ResponseBody,
        extensions: new Dictionary<string, object?>
        {
            ["facebookStatusCode"] = (int)ex.StatusCode,
            ["endpoint"] = ex.Endpoint,
            ["errorCode"] = ex.ErrorCode
        });
}

static int ReadIntQuery(string? value, int defaultValue, int minValue, int maxValue)
{
    if (!int.TryParse(value, out var parsed))
    {
        return defaultValue;
    }

    return Math.Clamp(parsed, minValue, maxValue);
}

app.Run();

static void LoadDotEnv()
{
    var currentDirectory = Directory.GetCurrentDirectory();
    var directory = new DirectoryInfo(currentDirectory);

    while (directory is not null)
    {
        var path = Path.Combine(directory.FullName, ".env");
        if (!File.Exists(path))
        {
            directory = directory.Parent;
            continue;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
            {
                value = value[1..^1];
            }

            if (!string.IsNullOrWhiteSpace(key))
            {
                Environment.SetEnvironmentVariable(key, value);

                var configurationKey = MapEnvKeyToConfigurationKey(key);
                if (!string.IsNullOrWhiteSpace(configurationKey) && !string.Equals(configurationKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable(configurationKey, value);
                }
            }
        }

        return;
    }
}

static string? MapEnvKeyToConfigurationKey(string key)
{
    return key.ToUpperInvariant() switch
    {
        "FACEBOOK_APP_ID" => "Facebook__AppId",
        "FACEBOOK_APP_SECRET" => "Facebook__AppSecret",
        "FACEBOOK_VERIFY_TOKEN" => "Facebook__VerifyToken",
        "FACEBOOK_PAGE_ID" => "Facebook__PageId",
        "FACEBOOK_PAGE_ACCESS_TOKEN" => "Facebook__PageAccessToken",
        "FACEBOOK_GRAPH_API_VERSION" => "Facebook__GraphApiVersion",
        "FACEBOOK_SUBSCRIBED_FIELDS" => "Facebook__SubscribedFields",
        _ => key
    };
}