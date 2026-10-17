using Hangfire;
using Microsoft.AspNetCore.Mvc;
using OCREngine.Applications.Jobs;
using OCREngine.Models;
using OCREngine.Utils;
using System.Text.RegularExpressions;
namespace OCREngine.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OcrController : ControllerBase
{
    private readonly ILogger<OcrController> _logger;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IRedisService _redisService;

    public OcrController(
        ILogger<OcrController> logger,
        IBackgroundJobClient backgroundJobClient,
        IRedisService redisService)
    {
        _logger = logger;
        _backgroundJobClient = backgroundJobClient;
        _redisService = redisService;
    }

    [HttpGet("supported-models")]
    public IActionResult GetSupportedModels()
    {
        return Ok(LlmUtil.supportedModels);
    }

    [HttpPost("process")]
    public async Task<IActionResult> ProcessOcr([FromBody] OcrUploadRequest request)
    {
        var bucket = request.Bucket;
        var objectKey = request.ObjectKey;
        var modelId = request.ModelId;

        if (string.IsNullOrEmpty(bucket))
            return BadRequest("Bucket is required.");
        if (string.IsNullOrEmpty(objectKey))
            return BadRequest("ObjectKey is required.");
        if (!LlmUtil.IsSupported(modelId))
            return BadRequest($"ModelId '{modelId}' is not supported. Supported models: {string.Join(", ", LlmUtil.supportedModels)}");

        string serverName = Regex.Replace(Environment.MachineName, "[^a-zA-Z0-9]", "").ToLowerInvariant();
        string taskId = $"{serverName}-{Guid.NewGuid()}";

        try
        {
            // Enqueue Background Job with model-specific queue
            string queueName = modelId.Trim().ToLowerInvariant();

            _logger.LogInformation("Enqueuing OCR job for TaskId: {TaskId}, Model: {ModelId}, Queue: {Queue}, Bucket: {Bucket}, ObjectKey: {ObjectKey}",
                taskId, modelId, queueName, bucket, objectKey);

            string jobId = _backgroundJobClient.Enqueue<OcrBackgroundJob>(
                queueName,
                job => job.ProcessOcrTaskAsync(taskId, bucket, objectKey, modelId, JobCancellationToken.Null));

            await FileUtil.SaveJobMapping(taskId, jobId);

            return Ok(new { TaskId = taskId, Message = "File queued for processing from S3 storage." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enqueuing OCR job");
            return StatusCode(500, "Internal server error.");
        }
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> CancelJob([FromQuery] string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return BadRequest("TaskId is required.");

        // 1. Lấy JobId từ mapping file
        string? jobId = await FileUtil.GetJobIdByTaskId(taskId);

        if (string.IsNullOrEmpty(jobId))
        {
            return NotFound(new
            {
                Message = $"Task {taskId} not found. It may have already completed or never existed."
            });
        }

        bool deletedFromQueue = false;
        bool removedFromRedis = false;

        // 2. Thử delete job khỏi Hangfire (job đang trong queue)
        try
        {
            deletedFromQueue = BackgroundJob.Delete(jobId);

            if (deletedFromQueue)
            {
                _logger.LogInformation("Deleted job from queue: TaskId={TaskId}, JobId={JobId}", taskId, jobId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete job from Hangfire: TaskId={TaskId}, JobId={JobId}", taskId, jobId);
        }

        // 3. Thử xóa worker khỏi Redis (job đang chạy)
        removedFromRedis = await _redisService.RemoveWorkerFromAllModelsAsync(taskId);

        if (removedFromRedis)
        {
            _logger.LogInformation("Removed worker from Redis: TaskId={TaskId}", taskId);
        }

        // 4. Xóa file tạm và mapping sau khi đã gửi cancel signal
        await FileUtil.DeleteTempUploadFileAsync(taskId);

        // 5. Trả về status
        if (!removedFromRedis && !deletedFromQueue)
        {
            // Job không tìm thấy ở cả queue và Redis
            // Có thể đã hoàn thành trước khi cancel
            return Ok(new
            {
                Message = $"Task {taskId} may have already completed.",
                Status = "Completed"
            });
        }

        string status = removedFromRedis
            ? "Running-Canceling"
            : "Queued-Canceled";

        return Ok(new
        {
            Message = $"Cancellation signal sent for Task {taskId}",
            Status = status,
            RemovedFromRedis = removedFromRedis,
            DeletedFromQueue = deletedFromQueue
        });
    }

    [HttpGet("get-markdown/{taskId}")]
    public IActionResult GetMarkdown(string taskId)
    {
        var filePath = FileUtil.GetJsonResultFilePath(taskId);
        if (filePath == null)
        {
            return NotFound("JSON result file not found or task not completed.");
        }

        // Thực hiện xóa file sau khi response đã hoàn tất gửi cho client
        Response.OnCompleted(() =>
        {
            FileUtil.DeleteFileSafe(filePath);
            return Task.CompletedTask;
        });

        return PhysicalFile(filePath, "application/json", Path.GetFileName(filePath));
    }
}
