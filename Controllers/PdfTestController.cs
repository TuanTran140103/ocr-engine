using Microsoft.AspNetCore.Mvc;
using OCREngine.Helpers;
using OCREngine.Utils;
using SkiaSharp;

namespace OCREngine.Controllers;

/// <summary>
/// Controller for testing PdfHelper functionality.
/// Used for development and debugging only.
/// </summary>
[ApiController]
[Route("api/test/pdf")]
public class PdfTestController : ControllerBase
{
    private readonly ILogger<PdfTestController> _logger;
    private readonly string _testOutputDir;

    public PdfTestController(ILogger<PdfTestController> logger)
    {
        _logger = logger;
        _testOutputDir = FileUtil.GetPdfTestPath();
    }

    /// <summary>
    /// Test RenderPdfPageAsync - Render single PDF page to SKBitmap
    /// Returns image and also saves to tmp_pdf_test/ directory
    /// </summary>
    [HttpPost("render-page")]
    public async Task<IActionResult> RenderPage(
        [FromForm] IFormFile file,
        [FromForm] int pageIndex = 0,
        [FromForm] int targetDpi = 200)
    {
        try
        {
            // Use stream overload to avoid temp file
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            var bitmap = await PdfHelper.RenderPdfPageAsync(memoryStream, pageIndex, targetDpi);

            // Convert bitmap to image for response
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var imageBytes = data.ToArray();

            // Also save to test output directory
            if (!Directory.Exists(_testOutputDir))
            {
                Directory.CreateDirectory(_testOutputDir);
            }

            var savedFileName = $"page_{pageIndex}_render.png";
            var savedFilePath = Path.Combine(_testOutputDir, savedFileName);
            await System.IO.File.WriteAllBytesAsync(savedFilePath, imageBytes);

            _logger.LogInformation("Rendered page {PageIndex} to {FilePath}", pageIndex, savedFilePath);

            Response.ContentType = "image/png";
            Response.Headers.Append("X-Page-Index", pageIndex.ToString());
            Response.Headers.Append("X-Dimensions", $"{bitmap.Width}x{bitmap.Height}");
            Response.Headers.Append("X-Saved-Path", savedFilePath);

            return File(imageBytes, "image/png", $"page_{pageIndex}.png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering PDF page");
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Test RenderPdfToLocalAsync - Render all PDF pages and save to local directory
    /// </summary>
    [HttpPost("render-to-local")]
    public async Task<IActionResult> RenderToLocal(
        [FromForm] IFormFile file,
        [FromForm] int targetDpi = 200,
        [FromForm] string format = "jpeg",
        [FromForm] int quality = 95)
    {
        string? tempPath = null;
        try
        {
            // Clean old test output
            if (Directory.Exists(_testOutputDir))
            {
                Directory.Delete(_testOutputDir, true);
            }

            tempPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.pdf");
            await using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var savedPaths = await PdfHelper.RenderPdfToLocalAsync(
                tempPath,
                _testOutputDir,
                targetDpi,
                format,
                quality);

            _logger.LogInformation("Rendered {PageCount} pages to {OutputDir}", 
                savedPaths.Count, _testOutputDir);

            // Return paths and also allow downloading
            return Ok(new
            {
                pageCount = savedPaths.Count,
                outputDirectory = _testOutputDir,
                files = savedPaths.Select(p => new
                {
                    fileName = Path.GetFileName(p),
                    absolutePath = p,
                    downloadUrl = $"/api/test/pdf/download?file={Path.GetFileName(p)}"
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering PDF to local");
            return BadRequest(new { error = ex.Message });
        }
        finally
        {
            // Clean up temp file
            if (tempPath != null && System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Download rendered image from test output directory
    /// </summary>
    [HttpGet("download")]
    public IActionResult DownloadImage([FromQuery] string file)
    {
        try
        {
            var filePath = Path.Combine(_testOutputDir, file);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound(new { error = $"File '{file}' not found in test output directory" });
            }

            var contentType = Path.GetExtension(file).ToLower() switch
            {
                ".png" => "image/png",
                ".jpeg" or ".jpg" => "image/jpeg",
                _ => "application/octet-stream"
            };

            return PhysicalFile(filePath, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading test image");
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Test GetPageCountAsync - Get number of pages in PDF
    /// </summary>
    [HttpPost("page-count")]
    public async Task<IActionResult> GetPageCount([FromForm] IFormFile file)
    {
        try
        {
            // Use stream overload to avoid temp file
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            var pageCount = await PdfHelper.GetPageCountAsync(memoryStream);

            return Ok(new { pageCount, fileName = file.FileName });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting page count");
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Test GetPageDimensionsAsync - Get dimensions of a specific page
    /// </summary>
    [HttpPost("page-dimensions")]
    public async Task<IActionResult> GetPageDimensions(
        [FromForm] IFormFile file,
        [FromForm] int pageIndex = 0)
    {
        string? tempPath = null;
        try
        {
            tempPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.pdf");
            await using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var (width, height) = await PdfHelper.GetPageDimensionsAsync(tempPath, pageIndex);

            return Ok(new
            {
                pageIndex,
                width,
                height,
                aspectRatio = Math.Round((double)width / height, 2),
                fileName = file.FileName
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting page dimensions");
            return BadRequest(new { error = ex.Message });
        }
        finally
        {
            if (tempPath != null && System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Test RenderThumbnailAsync - Render thumbnail for orientation detection
    /// </summary>
    [HttpPost("render-thumbnail")]
    public async Task<IActionResult> RenderThumbnail(
        [FromForm] IFormFile file,
        [FromForm] int pageIndex = 0)
    {
        string? tempPath = null;
        try
        {
            tempPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.pdf");
            await using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var imageBytes = await PdfHelper.RenderThumbnailAsync(tempPath, pageIndex);

            Response.ContentType = "image/jpeg";
            Response.Headers.Append("X-Thumbnail-Size", imageBytes.Length.ToString());

            return File(imageBytes, "image/jpeg", $"thumbnail_{pageIndex}.jpg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering thumbnail");
            return BadRequest(new { error = ex.Message });
        }
        finally
        {
            if (tempPath != null && System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Test RenderPdfPageAsync with Stream overload
    /// </summary>
    [HttpPost("render-from-stream")]
    public async Task<IActionResult> RenderFromStream(
        [FromForm] IFormFile file,
        [FromForm] int pageIndex = 0,
        [FromForm] int targetDpi = 200)
    {
        try
        {
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            var bitmap = await PdfHelper.RenderPdfPageAsync(memoryStream, pageIndex, targetDpi);

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            Response.ContentType = "image/png";
            Response.Headers.Append("X-Source", "stream");
            Response.Headers.Append("X-Dimensions", $"{bitmap.Width}x{bitmap.Height}");

            return File(data.ToArray(), "image/png", $"page_{pageIndex}_from_stream.png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering PDF from stream");
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Test RenderPdfPageAsync with PageDimensions overload
    /// </summary>
    [HttpPost("render-with-dimensions")]
    public async Task<IActionResult> RenderWithDimensions(
        [FromForm] IFormFile file,
        [FromForm] int pageIndex = 0,
        [FromForm] int width = 800,
        [FromForm] int height = 600)
    {
        string? tempPath = null;
        try
        {
            tempPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.pdf");
            await using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var dimensions = new Docnet.Core.Models.PageDimensions(width, height);
            var bitmap = await PdfHelper.RenderPdfPageAsync(tempPath, pageIndex, dimensions);

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            Response.ContentType = "image/png";
            Response.Headers.Append("X-Requested-Dimensions", $"{width}x{height}");
            Response.Headers.Append("X-Actual-Dimensions", $"{bitmap.Width}x{bitmap.Height}");

            return File(data.ToArray(), "image/png", $"page_{pageIndex}_custom_dim.png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering PDF with custom dimensions");
            return BadRequest(new { error = ex.Message });
        }
        finally
        {
            if (tempPath != null && System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Clean up test output directory
    /// </summary>
    [HttpDelete("cleanup")]
    public IActionResult Cleanup()
    {
        try
        {
            if (Directory.Exists(_testOutputDir))
            {
                Directory.Delete(_testOutputDir, true);
                _logger.LogInformation("Cleaned up test output directory");
            }

            return Ok(new { message = "Test output directory cleaned", directory = _testOutputDir });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up test directory");
            return BadRequest(new { error = ex.Message });
        }
    }
}
