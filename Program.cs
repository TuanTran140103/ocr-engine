using Hangfire;
using Hangfire.Redis.StackExchange;
using OCREngine.Applications.Interfaces;
using OCREngine.Applications.Jobs;
using OCREngine.Factories;
using OCREngine.Infrastructure.Filters;
using OCREngine.Infrastructure.Services;
using OCREngine.Infrastructure.ExternalService;
using OCREngine.Models.Enum;
using OCREngine.Options;
using Serilog;
using StackExchange.Redis;
using Scalar.AspNetCore;
using dotenv.net;
using OCREngine.Utils;


// Load .env into process environment variables BEFORE CreateBuilder
// so builder.Configuration can pick them up via AddEnvironmentVariables()
DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog from appsettings.json
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();

builder.Configuration.AddEnvironmentVariables();

builder.Host.UseSerilog(); // Replace default logger with Serilog

try
{
    Log.Information("Starting OCREngine application");

    // Clean up stale temp files on startup (leftover from crashed/killed jobs)
    // Native crashes (0xC0000005) kill the process immediately — DisposeAsync/finally never runs.
    try
    {
        FileUtil.CleanupAllStartupTempFiles(maxAgeHours: 1);
        Log.Information("Cleaned up old temp files on startup");
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to clean up temp directories on startup");
    }

    // Add services to the container.

    // Options
    builder.Services.Configure<LlmModelsOption>(builder.Configuration.GetSection("LlmModels"));
    builder.Services.Configure<HangfireOption>(builder.Configuration.GetSection("Hangfire"));
    builder.Services.Configure<ExternalServiceOption>(builder.Configuration.GetSection("ExternalServices"));
    builder.Services.Configure<S3Options>(builder.Configuration.GetSection("S3"));
    var s3Opts = builder.Configuration.GetSection("S3").Get<S3Options>();
    if (s3Opts != null)
    {
        Log.Information("S3 Config: ServiceUrl={Url}, Region={Region}, ForcePathStyle={ForcePathStyle}, AccessKey={Ak}, SecretKey={Sk}",
            s3Opts.ServiceUrl, s3Opts.Region, s3Opts.ForcePathStyle,
            s3Opts.AccessKey, s3Opts.SecretKey);
    }

    // Redis & Services
    var redisConn = builder.Configuration.GetSection("Hangfire:RedisConnection").Value
                    ?? builder.Configuration.GetConnectionString("Redis")
                    ?? "localhost";

    builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConn));
    builder.Services.AddSingleton<IRedisService, RedisService>();
    builder.Services.AddSingleton<IS3Service, S3Service>();
    builder.Services.AddSingleton<OpenAiClientFactory>();


    // Configure CORS for internal service
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowAny", policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    // Background Cleanup Service
    builder.Services.AddHostedService<WorkerLifetimeService>();
    builder.Services.AddScoped<OcrBackgroundJob>();

    // OCR services as Keyed Services

    builder.Services.AddKeyedScoped<IBaseOcrEngine, DeepSeekOcrService>(LlmSupport.DeepSeekOcr);
    builder.Services.AddKeyedScoped<IBaseOcrEngine, ChandraOcrService>(LlmSupport.ChandraOcr);
    builder.Services.AddKeyedScoped<IBaseOcrEngine, DotsOcrService>(LlmSupport.DotsOcr);

    builder.Services.AddHttpClient("OpenAIProvider", client =>
    {
        client.Timeout = TimeSpan.FromMinutes(10);
    });

    builder.Services.AddHttpClient("DocOriClient", client =>
    {
        client.Timeout = TimeSpan.FromMinutes(2);
    });

    builder.Services.AddHttpClient("DeepSeekOcr", client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });

    builder.Services.AddHttpClient("ChandraOcr", client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });

    builder.Services.AddHttpClient("DotsOcr", client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });

    builder.Services.AddScoped<IDocOriService, DocOriService>();

    // Hangfire Configuration
    var hangfireConfig = builder.Configuration.GetSection("Hangfire").Get<HangfireOption>();
    if (hangfireConfig != null)
    {
        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseRedisStorage(
                ConnectionMultiplexer.Connect(hangfireConfig.RedisConnection),
                new RedisStorageOptions
                {
                    Prefix = "ocrengine:hangfire:"
                })
        );

        var ocrQueueNames = new[] { "deepseekocr", "chandraocr", "dotsocr" };

        foreach (var queue in ocrQueueNames)
        {
            builder.Services.AddHangfireServer(options =>
            {
                options.WorkerCount = hangfireConfig.WorkerCount;
                options.ServerName = $"OCREngine-WORKER-{Environment.MachineName}-{queue}";
                options.Queues = new[] { queue };
            });
        }
        Log.Information("Hangfire configured with shared worker pool for queues: deepseekocr, chandraocr, dotsocr");
    }

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi();

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference("/docs", options =>
        {
            options.WithTitle("OCREngine API Reference")
                   .WithTheme(ScalarTheme.Mars)
                   .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
        });
    }

    app.UseCors("AllowAny");

    app.UseAuthorization();

    // Hangfire Dashboard
    if (hangfireConfig != null)
    {
        app.UseHangfireDashboard(hangfireConfig.DashboardPath, new DashboardOptions
        {
            DashboardTitle = hangfireConfig.DashboardTitle,
            StatsPollingInterval = 5000, // Refresh every 5 seconds
            // For development - allow all. In production, add authorization
            Authorization = new[] { new HangfireAuthorizationFilter() }
        });
        Log.Information("Hangfire Dashboard available at {DashboardPath}", hangfireConfig.DashboardPath);
    }

    app.UseSerilogRequestLogging(); // Enable Serilog's efficient request logging
    app.MapControllers();

    // Health check endpoint
    app.MapGet("/health", async (IConnectionMultiplexer redis) =>
    {
        var checks = new Dictionary<string, object>();
        var healthy = true;

        try
        {
            await redis.GetDatabase().PingAsync();
            checks["redis"] = "healthy";
        }
        catch (Exception ex)
        {
            healthy = false;
            checks["redis"] = $"unhealthy: {ex.Message}";
        }

        return Results.Json(new
        {
            status = healthy ? "healthy" : "unhealthy",
            timestamp = DateTime.UtcNow,
            checks
        }, statusCode: healthy ? 200 : 503);
    });

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
