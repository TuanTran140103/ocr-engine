using Microsoft.AspNetCore.Mvc;
using OCREngine.Applications.Interfaces;
using OCREngine.Helpers;
using OCREngine.Models;
using OCREngine.Models.Enum;
using OCREngine.Utils;
using System.Text.Encodings.Web;
using System.Text.Json;
using SkiaSharp;

namespace OCREngine.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TestController : ControllerBase
{
    private readonly ILogger<TestController> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRedisService _redisService;

    public TestController(
        ILogger<TestController> logger,
        IServiceProvider serviceProvider,
        IRedisService redisService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _redisService = redisService;
    }

    /// <summary>
    /// Xử lý ảnh (resize, rotate, encode) dùng ImageHelper.
    /// </summary>
    private static async Task<ProcessedImage> ProcessImageAsync(
        Stream stream,
        bool useOriginalImage,
        int targetDpi = 200,
        int minImageDim = 28,
        float rotationDegrees = 0,
        bool usePng = false)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var imageBytes = ms.ToArray();

        if (useOriginalImage)
        {
            var base64 = Convert.ToBase64String(imageBytes);
            var dims = ImageHelper.GetImageDimensions(imageBytes);
            return new ProcessedImage { Base64 = base64, Width = dims.Width, Height = dims.Height };
        }

        using var bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap == null)
            throw new InvalidOperationException("Failed to decode image.");

        var processed = bitmap;
        int width = bitmap.Width;
        int height = bitmap.Height;

        // Rotate nếu cần
        if (rotationDegrees != 0)
        {
            using var rotated = ImageHelper.Rotate(processed, rotationDegrees);
            processed.Dispose();
            processed = rotated;
            width = rotated.Width;
            height = rotated.Height;
        }

        // Resize theo DPI (giả sử 96 DPI là mặc định màn hình)
        float scale = targetDpi / 96f;
        if (scale != 1f)
        {
            using var scaled = ImageHelper.Scale(processed, scale);
            processed.Dispose();
            processed = scaled;
            width = scaled.Width;
            height = scaled.Height;
        }

        // Ensure minimum dimension
        if (minImageDim > 0)
        {
            using var minDim = ImageHelper.EnsureMinDimension(processed, minImageDim);
            processed.Dispose();
            processed = minDim;
            width = minDim.Width;
            height = minDim.Height;
        }

        var base64Result = ImageHelper.EncodeToBase64(processed, usePng);
        processed.Dispose();

        return new ProcessedImage { Base64 = base64Result, Width = width, Height = height };
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok("Server is running.");
    }

    [HttpGet("supported-models")]
    public IActionResult GetSupportedModels()
    {
        return Ok(LlmUtil.supportedModels);
    }

    /// <summary>
    /// Test OCR synchronous trên 1 ảnh đơn lẻ (không qua Hangfire).
    /// </summary>
    [HttpPost("ocr-image")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> OcrImage([FromForm] OcrTestImageRequest request)
    {
        var file = request.File;
        var modelId = request.ModelId;
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        var modelEnumVal = LlmUtil.GetModelEnum(modelId);
        if (modelEnumVal == null)
            return BadRequest($"ModelId '{modelId}' is not supported. Supported models: {string.Join(", ", LlmUtil.supportedModels)}");

        var modelEnum = modelEnumVal.Value;
        var ocrEngine = _serviceProvider.GetKeyedService<IBaseOcrEngine>(modelEnum);
        if (ocrEngine == null)
            return StatusCode(500, $"OCR Engine for {modelId} not found.");

        try
        {
            bool usePng = modelEnum == LlmSupport.DeepSeekOcr;

            // Xử lý ảnh
            using var stream = file.OpenReadStream();
            var processedImage = await ProcessImageAsync(
                stream,
                request.UseOriginalImage,
                targetDpi: request.TargetDpi,
                minImageDim: request.MinImageDim,
                rotationDegrees: request.RotationDegrees,
                usePng: usePng);

            var taskId = $"test-{Guid.NewGuid()}";

            if (request.SaveProcessedImage)
            {
                string fileName = $"test_{file.FileName}";
                if (usePng && !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    fileName = Path.ChangeExtension(fileName, ".png");
                else if (!usePng && !fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                    fileName = Path.ChangeExtension(fileName, ".jpg");

                var filePath = await FileUtil.SaveDebugImageAsync(processedImage.Base64, taskId, fileName);
                _logger.LogInformation("Saved debug image to {FilePath}", filePath);
            }

            var ocrRequest = new OcrImageRequest
            {
                TaskId = taskId,
                Image = processedImage,
                PageIndex = request.PageIndex,
                RotationDegrees = (int)request.RotationDegrees
            };

            _logger.LogInformation("Starting synchronous OCR for Model: {ModelId}", modelId);
            var blocks = await ocrEngine.OcrImageAsync(ocrRequest, default);
            var pageResult = await ocrEngine.ConvertPageToMarkdownAsync(
                blocks, processedImage.Base64, 0, includeHeaderFooter: true, processedImage.ContentType);

            // Save markdown to tmp file
            try
            {
                string jsonFileName = $"test_{Guid.NewGuid()}_{Path.GetFileNameWithoutExtension(file.FileName)}.json";
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true
                };
                var jsonContent = JsonSerializer.Serialize(pageResult, jsonOptions);
                var jsonFilePath = await FileUtil.SaveDebugJsonAsync(jsonContent, jsonFileName);
                _logger.LogInformation("Saved debug JSON to {FilePath}", jsonFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save debug JSON file");
            }

            // Save cropped images to debug folder
            if (request.SaveProcessedImage && pageResult.Images != null && pageResult.Images.Count > 0)
            {
                try
                {
                    foreach (var kvp in pageResult.Images)
                    {
                        string imageKey = kvp.Key;
                        string localFileName = Path.GetFileName(imageKey);
                        var imageFilePath = await FileUtil.SaveDebugImageAsync(kvp.Value, taskId, localFileName);
                        _logger.LogInformation("Saved cropped image to {FilePath}", imageFilePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save cropped images");
                }
            }

            return Ok(pageResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing synchronous OCR for file {FileName}", file.FileName);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Xóa tất cả Redis streams (dùng cho debugging).
    /// </summary>
    [HttpDelete("clear-streams")]
    public async Task<IActionResult> ClearStreams()
    {
        await _redisService.ClearAllStreamsAsync();
        return Ok(new { Message = "All ocr:events:stream keys have been cleared from Redis." });
    }
}
