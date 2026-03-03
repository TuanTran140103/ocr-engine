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
using System.Runtime.Versioning;
using StackExchange.Redis;
using Scalar.AspNetCore;
using dotenv.net;


[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("linux")]
DotEnv.Load();


var builder = WebApplication.CreateBuilder(args);

// Configure Serilog from appsettings.json
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();

builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

builder.Configuration.AddEnvironmentVariables();

builder.Host.UseSerilog(); // Replace default logger with Serilog

try
{
    Log.Information("Starting OCREngine application");

    // Clean up tmp_upload directory on startup
    try
    {
        string tmpUploadPath = Path.Combine(Directory.GetCurrentDirectory(), "tmp_upload");
        if (Directory.Exists(tmpUploadPath))
        {
            var files = Directory.GetFiles(tmpUploadPath);
            foreach (var file in files)
            {
                File.Delete(file);
            }
            Log.Information("Cleaned up {Count} files in tmp_upload directory", files.Length);
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to clean up TmpUpload directory on startup");
    }

    // Add services to the container.

    // Options
    builder.Services.Configure<LlmModelsOption>(builder.Configuration.GetSection("LlmModels"));
    builder.Services.Configure<HangfireOption>(builder.Configuration.GetSection("Hangfire"));
    builder.Services.Configure<ExternalServiceOption>(builder.Configuration.GetSection("ExternalServices"));

    // Redis & Services
    var redisConn = builder.Configuration.GetSection("Hangfire:RedisConnection").Value
                    ?? builder.Configuration.GetConnectionString("Redis")
                    ?? "localhost";

    builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConn));
    builder.Services.AddSingleton<IRedisService, RedisService>();
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
    builder.Services.AddKeyedScoped<IBaseOcrEngine, DotsOcrService>(LlmSupport.Dots);
    // TODO: Add ChandraOcrService when implemented
    builder.Services.AddKeyedScoped<IBaseOcrEngine, ChandraOcrService>(LlmSupport.Chandra);
    builder.Services.AddKeyedScoped<IBaseOcrEngine, DeepSeekOcrService>(LlmSupport.DeepSeekOcr);

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

        var ocrQueueNames = new[] { "dots", "chandra", "deepseekocr" };

        foreach (var queue in ocrQueueNames)
        {
            builder.Services.AddHangfireServer(options =>
            {
                options.WorkerCount = hangfireConfig.WorkerCount;  // chung 1 giá trị
                options.ServerName = $"OCREngine-WORKER-{Environment.MachineName}-{queue}";
                options.Queues = new[] { queue };
            });
        }
        Log.Information("Hangfire configured with shared worker pool for queues: dots, chandra, deepseekocr");
    }

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi(options =>
    {
        options.AddSchemaTransformer((schema, context, cancellationToken) =>
        {
            if (context.JsonTypeInfo.Type == typeof(IFormFile) || context.JsonTypeInfo.Type == typeof(IFormFileCollection))
            {
                schema.Type = "string";
                schema.Format = "binary";
            }
            return Task.CompletedTask;
        });
    });

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
