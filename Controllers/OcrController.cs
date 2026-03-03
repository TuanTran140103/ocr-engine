using Hangfire;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc;
using OCREngine.Applications.Jobs;
using OCREngine.Applications.Interfaces;
using OCREngine.Helpers;
using OCREngine.Models;
using OCREngine.Models.Enum;
using OCREngine.Utils;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
namespace OCREngine.Controllers;

[ApiController]
[Route("api/[controller]")]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public class OcrController : ControllerBase
{
    private readonly ILogger<OcrController> _logger;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IRedisService _redisService;
    private const string TEMP_UPLOAD_DIR = "tmp_upload";
    private const string DEBUG_DIR = "tmp_debug";

    public OcrController(
        ILogger<OcrController> logger,
        IBackgroundJobClient backgroundJobClient,
        IRedisService redisService)
    {
        _logger = logger;
        _backgroundJobClient = backgroundJobClient;
        _redisService = redisService;

        var uploadPath = Path.Combine(Directory.GetCurrentDirectory(), TEMP_UPLOAD_DIR);
        if (!Directory.Exists(uploadPath))
            Directory.CreateDirectory(uploadPath);

        var debugPath = Path.Combine(Directory.GetCurrentDirectory(), DEBUG_DIR);
        if (!Directory.Exists(debugPath))
            Directory.CreateDirectory(debugPath);
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
        string uploadPath = Path.Combine(Directory.GetCurrentDirectory(), TEMP_UPLOAD_DIR);
        string filePath = Path.Combine(uploadPath, $"{taskId}_{originalFileName}");

        // Check if file essentially exists
        var existingFile = Directory.GetFiles(uploadPath, $"*_{originalFileName}");
        if (existingFile.Length > 0)
        {
            return Conflict($"File '{originalFileName}' is already being processed or exists in temporary storage.");
        }

        try
        {
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            // Enqueue Background Job with model-specific queue
            string queueName = modelId.Trim().ToLowerInvariant();

            _logger.LogInformation("Enqueuing OCR job for TaskId: {TaskId}, Model: {ModelId}, Queue: {Queue}",
                taskId, modelId, queueName);

            _backgroundJobClient.Enqueue<OcrBackgroundJob>(
                queueName,
                job => job.ProcessOcrTaskAsync(taskId, filePath, modelId, JobCancellationToken.Null));


            return Ok(new { TaskId = taskId, Message = "File uploaded and processing started." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file upload");
            // Clean up if failed
            if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
            return StatusCode(500, "Internal server error during upload.");
        }
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> CancelJob([FromQuery] string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return BadRequest("TaskId is required.");

        // Iterate all models and try to remove the task field from the worker pool
        bool removed = await _redisService.RemoveWorkerFromAllModelsAsync(taskId);

        if (!removed)
        {
            return NotFound(new { Message = $"Task {taskId} not found in any model or already completed." });
        }

        return Ok(new { Message = $"Cancellation signal sent for Task {taskId} (scanned all models)" });
    }

    [HttpGet("get-markdown/{taskId}")]
    public IActionResult GetMarkdown(string taskId)
    {
        var filePath = FileUtil.GetMarkdownFilePath(taskId);
        if (filePath == null)
        {
            return NotFound("Markdown file not found or task not completed.");
        }

        // Thực hiện xóa file sau khi response đã hoàn tất gửi cho client
        Response.OnCompleted(() =>
        {
            try
            {
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete temporary file after download: {FilePath}", filePath);
            }
            return Task.CompletedTask;
        });

        return PhysicalFile(filePath, "text/markdown", Path.GetFileName(filePath));
    }

    // for test
    [HttpDelete("clear-streams")]
    public async Task<IActionResult> ClearStreams()
    {
        await _redisService.ClearAllStreamsAsync();
        return Ok(new { Message = "All ocr:stream:* keys have been cleared from Redis." });
    }
}
