using core_service.Data;
using core_service.Services;
using core_service.Workers;
using core_service.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

// Load .env variables from parent directory if it exists
LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);

// Configure Entity Framework Core with SQLite
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=app.db")
           .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

// Register Services
builder.Services.AddHttpClient<IAiAnalysisService, OpenAIAnalysisService>();
builder.Services.Configure<ModerationOptions>(builder.Configuration.GetSection(ModerationOptions.SectionName));
builder.Services.AddSingleton<EventProcessingPipeline>();
builder.Services.AddSingleton<IReplyCommandPublisher, ReplyCommandPublisher>();
builder.Services.AddSingleton<IAutoReplyService, AutoReplyService>();

// Register Worker
builder.Services.AddHostedService<EventConsumerWorker>();

var host = builder.Build();

// Run Migrations on startup
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        // In development it's helpful to continue even if migrations fail for
        // reasons like model snapshot drift. Log and fall back to EnsureCreated
        // so local runs are easier to get running.
        var logger = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Program");
        logger?.LogWarning(ex, "Migrations failed or mismatch detected. Falling back to EnsureCreated() for local dev run.");
        try
        {
            db.Database.EnsureCreated();
        }
        catch (Exception inner)
        {
            logger?.LogError(inner, "Failed to EnsureCreated database after migration error.");
            throw;
        }
    }
}

host.MapGet("/", () => Results.Ok(new { service = "core-service", status = "running" }));
host.MapGet("/health", () => Results.Ok(new { service = "core-service", status = "healthy" }));

host.Run();

static void LoadDotEnv()
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".env"))
    };

    foreach (var path in candidates)
    {
        if (!File.Exists(path))
        {
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
            }
        }
        
        // Also add them to configuration builder environment variables implicitly,
        // but normally setting Environment is enough for HostBuilder to pick them up if prefixed correctly.
        // For general usage we rely on Environment.GetEnvironmentVariable or configuration provider.
        return;
    }
}
