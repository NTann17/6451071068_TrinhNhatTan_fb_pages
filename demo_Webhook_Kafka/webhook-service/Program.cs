using System.Text;
using System.Text.Json;
using webhook_service.Models;
using webhook_service.Services;

LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FacebookOptions>(builder.Configuration.GetSection(FacebookOptions.SectionName));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));

builder.Services.AddHttpClient<IFacebookGraphApiClient, FacebookGraphApiClient>();
builder.Services.AddSingleton<IFacebookSignatureValidator, FacebookSignatureValidator>();
builder.Services.AddSingleton<IFacebookPayloadNormalizer, FacebookPayloadNormalizer>();
builder.Services.AddSingleton<IKafkaProducerService, KafkaProducerService>();
builder.Services.AddHostedService<FacebookSubscriptionHostedService>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "webhook-service", status = "running" }));

app.MapGet("/webhook", (HttpRequest request, IConfiguration configuration) =>
{
	var mode = request.Query["hub.mode"].ToString();
	var token = request.Query["hub.verify_token"].ToString();
	var challenge = request.Query["hub.challenge"].ToString();

	var verifyToken = configuration[$"{FacebookOptions.SectionName}:{nameof(FacebookOptions.VerifyToken)}"];
	if (mode == "subscribe" && !string.IsNullOrWhiteSpace(challenge) && token == verifyToken)
	{
		return Results.Text(challenge, "text/plain", Encoding.UTF8);
	}

	return Results.Unauthorized();
});

app.MapPost("/webhook", async (
	HttpContext context,
	IFacebookSignatureValidator signatureValidator,
	IFacebookPayloadNormalizer normalizer,
	IKafkaProducerService producer,
	ILoggerFactory loggerFactory,
	CancellationToken cancellationToken) =>
{
	var logger = loggerFactory.CreateLogger("WebhookEndpoint");

	context.Request.EnableBuffering();
	string payload;
	using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
	{
		payload = await reader.ReadToEndAsync(cancellationToken);
		context.Request.Body.Position = 0;
	}

	if (!signatureValidator.IsValidSignature(context.Request.Headers, payload))
	{
		logger.LogWarning("Invalid Facebook signature");
		return Results.Unauthorized();
	}

	JsonDocument document;
	try
	{
		document = JsonDocument.Parse(payload);
	}
	catch (JsonException ex)
	{
		logger.LogWarning(ex, "Invalid JSON payload");
		return Results.BadRequest("Invalid JSON payload");
	}

	using (document)
	{
		var normalizedEvents = normalizer.Normalize(document.RootElement);
		if (normalizedEvents.Count == 0)
		{
			logger.LogInformation("Webhook payload parsed but no supported events found");
			return Results.Ok(new { accepted = true, published = 0 });
		}

		await producer.PublishBatchAsync(normalizedEvents, cancellationToken);
		logger.LogInformation("Published {Count} normalized events to Kafka", normalizedEvents.Count);

		return Results.Ok(new { accepted = true, published = normalizedEvents.Count });
	}
});

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
		"KAFKA_BOOTSTRAP_SERVERS" => "Kafka__BootstrapServers",
		"KAFKA_TOPIC" => "Kafka__Topic",
		_ => key
	};
}
