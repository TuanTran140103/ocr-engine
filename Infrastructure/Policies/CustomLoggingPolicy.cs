using System.ClientModel.Primitives;
using System.Diagnostics;

namespace OCREngine.Infrastructure.Policies;

public class CustomLoggingPolicy : PipelinePolicy
{
    private readonly ILogger _logger;

    public CustomLoggingPolicy(ILogger logger)
    {
        _logger = logger;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        LogRequest(message);

        var stopwatch = Stopwatch.StartNew();
        ProcessNext(message, pipeline, currentIndex);
        stopwatch.Stop();

        LogResponse(message, stopwatch.Elapsed);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        LogRequest(message);

        var stopwatch = Stopwatch.StartNew();
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        stopwatch.Stop();

        LogResponse(message, stopwatch.Elapsed);
    }

    private void LogRequest(PipelineMessage message)
    {
        _logger.LogInformation("[SDK REQUEST] {Method} {Uri}", message.Request.Method, message.Request.Uri);
    }

    private void LogResponse(PipelineMessage message, TimeSpan duration)
    {
        if (message.Response != null)
        {
            _logger.LogInformation("[SDK RESPONSE] Status: {Status}, Duration: {Duration}ms", message.Response.Status, duration.TotalMilliseconds);
        }
        else
        {
            _logger.LogError("[SDK ERROR] No response received after {Duration}ms. (Potential Timeout)", duration.TotalMilliseconds);
        }
    }
}
