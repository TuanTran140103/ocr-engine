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
    private readonly IServiceProvider _serviceProvider;
    private const string TEMP_UPLOAD_DIR = "tmp_upload";
    private const string DEBUG_DIR = "tmp_debug";

    public OcrController(
        ILogger<OcrController> logger,
        IBackgroundJobClient backgroundJobClient,
        IRedisService redisService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _backgroundJobClient = backgroundJobClient;
        _redisService = redisService;
        _serviceProvider = serviceProvider;

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
            string queueName = modelId.ToLowerInvariant();
            _backgroundJobClient.Create<OcrBackgroundJob>(
                job => job.ProcessOcrTaskAsync(taskId, filePath, modelId, JobCancellationToken.Null),
                new EnqueuedState(queueName));

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

    // for test
    /// <summary>
    /// OCR đồng bộ một ảnh đơn lẻ để test pipeline và chất lượng.<br/>
    /// Pipeline: decode ảnh → resize (minDim + multi-28) → PNG encode → OCR engine → trả markdown.<br/>
    /// Nếu <c>SaveProcessedImage=true</c>, ảnh đã xử lý được lưu vào <c>tmp_debug/</c>.
    /// </summary>
    [HttpPost("test-image")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> TestImageOcr(
        [FromForm] OcrTestImageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.File == null || request.File.Length == 0)
            return BadRequest("No file uploaded.");

        if (!LlmUtil.IsSupported(request.ModelId))
            return BadRequest($"ModelId '{request.ModelId}' is not supported. " +
                              $"Supported: {string.Join(", ", LlmUtil.supportedModels)}");

        var modelEnumVal = LlmUtil.GetModelEnum(request.ModelId);
        if (modelEnumVal == null)
            return BadRequest($"Cannot resolve model enum for '{request.ModelId}'.");

        var modelEnum = modelEnumVal.Value;

        // --- 1. Xử lý File qua Pipeline chuẩn (PDF/Image -> WhiteBG -> Resize -> Multi-28) ---
        ProcessedImage? processedImage;
        bool usePng = (modelEnum == LlmSupport.DeepSeekOcr);
        try
        {
            using var stream = request.File.OpenReadStream();
            processedImage = await ImageHelper.ProcessFileAsync(
                stream,
                request.File.FileName,
                targetDpi: request.TargetDpi,
                minImageDim: request.MinImageDim,
                rotationDegrees: request.RotationDegrees,
                usePng: usePng);

            if (processedImage == null)
                return BadRequest("Failed to process file (render or decode failure).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TEST-IMAGE] Failed to process source file.");
            return BadRequest($"Processing failed: {ex.Message}");
        }

        // --- 2. Lưu ảnh đã xử lý để debug chất lượng ---
        string? savedImagePath = null;
        if (request.SaveProcessedImage)
        {
            try
            {
                string debugDir = Path.Combine(Directory.GetCurrentDirectory(), DEBUG_DIR);
                string baseName = Path.GetFileNameWithoutExtension(request.File.FileName);
                string ext = usePng ? "png" : "jpg";
                string debugFile = Path.Combine(debugDir,
                    $"{baseName}_{processedImage.Width}x{processedImage.Height}.{ext}");

                byte[] imageBytes = Convert.FromBase64String(processedImage.Base64);
                await System.IO.File.WriteAllBytesAsync(debugFile, imageBytes, cancellationToken);
                savedImagePath = debugFile;
                _logger.LogInformation("[TEST-IMAGE] Saved processed image: {Path}", debugFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TEST-IMAGE] Could not save processed image.");
            }
        }

        // --- 3. Gọi OCR engine ---
        var ocrEngine = _serviceProvider.GetKeyedService<IBaseOcrEngine>(modelEnum);
        if (ocrEngine == null)
            return StatusCode(500, $"OCR engine for model '{request.ModelId}' not registered.");

        List<LayoutBlock> pageBlocks;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var ocrRequest = new OcrImageRequest
            {
                Image = processedImage,
                RotationDegrees = request.RotationDegrees,
                PageIndex = 0
            };
            pageBlocks = await ocrEngine.OcrImageAsync(ocrRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TEST-IMAGE] OCR engine failed.");
            return StatusCode(500, $"OCR failed: {ex.Message}");
        }
        sw.Stop();

        // --- 4. Convert sang markdown ---
        string markdown = ocrEngine.ConvertPageToMarkdown(pageBlocks);

        _logger.LogInformation(
            "[TEST-IMAGE] Done. Model={Model}, Size={W}x{H}, Blocks={Blocks}, Time={Time:F2}s",
            request.ModelId, processedImage.Width, processedImage.Height,
            pageBlocks.Count, sw.Elapsed.TotalSeconds);

        return Ok(new
        {
            Model = request.ModelId,
            ImageWidth = processedImage.Width,
            ImageHeight = processedImage.Height,
            BlockCount = pageBlocks.Count,
            ProcessingTimeSec = Math.Round(sw.Elapsed.TotalSeconds, 2),
            SavedImagePath = savedImagePath,
            Markdown = markdown
        });
    }
}
