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
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ProcessOcr([FromForm] OcrUploadRequest request)
    {
        var file = request.File;
        var modelId = request.ModelId;
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        if (!LlmUtil.IsSupported(modelId))
            return BadRequest($"ModelId '{modelId}' is not supported. Supported models: {string.Join(", ", LlmUtil.supportedModels)}");

        string serverName = Regex.Replace(Environment.MachineName, "[^a-zA-Z0-9]", "").ToLowerInvariant();
        string taskId = $"{serverName}-{Guid.NewGuid()}";
        string originalFileName = Path.GetFileName(file.FileName);

        // Check if file essentially exists
        var uploadPath = FileUtil.GetTempUploadPath();
        var existingFile = Directory.GetFiles(uploadPath, $"*_{originalFileName}");
        if (existingFile.Length > 0)
        {
            return Conflict($"File '{originalFileName}' is already being processed or exists in temporary storage.");
        }

        try
        {
            // Save file using FileUtil
            string filePath = await FileUtil.SaveTempUploadFileAsync(file, taskId, originalFileName);

            // Enqueue Background Job with model-specific queue
            string queueName = modelId.Trim().ToLowerInvariant();

            _logger.LogInformation("Enqueuing OCR job for TaskId: {TaskId}, Model: {ModelId}, Queue: {Queue}",
                taskId, modelId, queueName);

            // Enqueue và lấy JobId từ Hangfire
            string jobId = _backgroundJobClient.Enqueue<OcrBackgroundJob>(
                queueName,
                job => job.ProcessOcrTaskAsync(taskId, filePath, modelId, JobCancellationToken.Null));

            // Lưu mapping taskId ↔ JobId
            await FileUtil.SaveJobMapping(taskId, jobId);

            return Ok(new { TaskId = taskId, Message = "File uploaded and queued." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file upload");
            // Clean up if failed
            await FileUtil.DeleteTempUploadFileAsync(taskId);
            return StatusCode(500, "Internal server error during upload.");
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
