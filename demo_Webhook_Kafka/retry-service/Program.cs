using retry_service.Services;

LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IRetryPublisher, RetryPublisher>();
builder.Services.AddHostedService<RetryWorker>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "retry-service", status = "healthy" }));

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
            }
        }

        return;
    }
}