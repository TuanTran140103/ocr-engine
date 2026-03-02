using Microsoft.AspNetCore.Mvc;
using OCREngine.Applications.Interfaces;

using OCREngine.Models;
using OCREngine.Helpers;
using SkiaSharp;

namespace OCREngine.Controllers;


// for test
[ApiController]
[Route("api/[controller]")]
public class OrientationController : ControllerBase
{
    private readonly IDocOriService _docOriService;
    private readonly ILogger<OrientationController> _logger;

    public OrientationController(IDocOriService docOriService, ILogger<OrientationController> logger)
    {
        _docOriService = docOriService;
        _logger = logger;
    }

    [HttpPost("predict")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Predict([FromForm] SingleOrientationRequest request)
    {
        var file = request.File;
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var originalBytes = ms.ToArray();

            // 1. Gửi ảnh trực tiếp tới external (original bytes)
            var result = await _docOriService.PredictAsync(originalBytes, file.FileName);

            // 2. Lớp tiền xử lý sau khi nhận được label (để kiểm tra chất lượng trước khi gửi vLLM)
            try
            {
                using var skBitmap = SKBitmap.Decode(originalBytes);
                if (skBitmap != null)
                {
                    float.TryParse(result.Orientation, out float degrees);

                    // Chạy qua ImageHelper.ProcessPdfPage (Tích hợp xoay + resize + encode)
                    var processedImage = ImageHelper.ProcessPdfPage(skBitmap, rotationDegrees: -degrees);

                    if (processedImage != null && !string.IsNullOrEmpty(processedImage.Base64))
                    {
                        var processedBytes = Convert.FromBase64String(processedImage.Base64);

                        // Lưu ảnh vLLM-quality để kiểm tra
                        var debugPath = Path.Combine(Directory.GetCurrentDirectory(), "tmp_debug");
                        if (!Directory.Exists(debugPath)) Directory.CreateDirectory(debugPath);

                        var debugFileName = $"vllm_ready_{result.Orientation}_{file.FileName}";
                        if (!debugFileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) debugFileName += ".png";

                        var fullDebugPath = Path.Combine(debugPath, debugFileName);
                        await System.IO.File.WriteAllBytesAsync(fullDebugPath, processedBytes);

                        _logger.LogInformation("Pre-processing complete. Quality check image saved to {Path}", fullDebugPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to run pre-processing layer for quality check.");
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during orientation prediction for file {FileName}", file.FileName);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("predict-batch")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> PredictBatch([FromForm] BatchOrientationRequest request)
    {
        var files = request.Files;
        if (files == null || files.Count == 0)
            return BadRequest("No files uploaded.");

        var tempFiles = new List<string>();
        try
        {
            var uploadPath = Path.Combine(Directory.GetCurrentDirectory(), "tmp_upload");
            if (!Directory.Exists(uploadPath)) Directory.CreateDirectory(uploadPath);

            foreach (var file in files)
            {
                var tempPath = Path.Combine(uploadPath, $"ori_{Guid.NewGuid()}_{file.FileName}");
                using (var stream = new FileStream(tempPath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }
                tempFiles.Add(tempPath);
            }

            var result = await _docOriService.PredictBatchAsync(tempFiles);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during batch orientation prediction");
            return StatusCode(500, new { error = ex.Message });
        }
        finally
        {
            foreach (var tempFile in tempFiles)
            {
                if (System.IO.File.Exists(tempFile))
                {
                    try { System.IO.File.Delete(tempFile); } catch { /* ignore */ }
                }
            }
        }
    }
}
