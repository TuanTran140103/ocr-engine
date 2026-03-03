using Hangfire;
using Microsoft.Extensions.Options;
using OCREngine.Applications.Interfaces;
using OCREngine.Models.Enum;
using OCREngine.Options;
using OCREngine.Helpers;
using OCREngine.Models;
using System.Text.Json;
using System.Diagnostics;
using System.Text;
using OCREngine.Utils;
using System.Runtime.Versioning;

namespace OCREngine.Applications.Jobs;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public class OcrBackgroundJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRedisService _redisService;
    private readonly ILogger<OcrBackgroundJob> _logger;
    private readonly LlmModelsOption _models;
    private readonly ExternalServiceOption _extOptions;
    private readonly IDocOriService _docOriService;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private int _completedPages = 0;
    private readonly List<object> _eventLogs = new();

    public OcrBackgroundJob(
        IServiceProvider serviceProvider,
        IRedisService redisService,
        ILogger<OcrBackgroundJob> logger,
        IOptions<LlmModelsOption> modelsOptions,
        IOptions<ExternalServiceOption> extOptions,
        IDocOriService docOriService)
    {
        _serviceProvider = serviceProvider;
        _redisService = redisService;
        _logger = logger;
        _models = modelsOptions.Value;
        _extOptions = extOptions.Value;
        _docOriService = docOriService;
    }

    /// <summary>
    /// Background task for OCR processing with distributed concurrency management.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task ProcessOcrTaskAsync(
        string taskId,
        string pathPdf,
        string modelId,
        IJobCancellationToken cancellationToken)
    {
        var token = cancellationToken.ShutdownToken;
        _completedPages = 0;
        _eventLogs.Clear();

        var stopwatch = Stopwatch.StartNew();

        // 1. Resolve Model and Concurrency Settings
        var modelEnumVal = LlmUtil.GetModelEnum(modelId);
        if (modelEnumVal == null)
        {
            await ReportEventAsync(taskId, pathPdf, "Invalid modelId", null, EventStatus.Failed);
            return;
        }
        var modelEnum = modelEnumVal.Value;

        string modelKey = modelEnum.ToString();
        var modelOption = modelEnum switch
        {
            LlmSupport.Dots => _models.Dots,
            LlmSupport.Chandra => _models.Chandra,
            LlmSupport.DeepSeekOcr => _models.DeepSeek,
            _ => null
        };

        if (modelOption == null)
        {
            await ReportEventAsync(taskId, pathPdf, $"Model configuration for {modelKey} not found", null, EventStatus.Failed);
            return;
        }

        int totalMax = modelOption.Concurrency;
        if (!File.Exists(pathPdf))
        {
            await ReportEventAsync(taskId, pathPdf, $"File not found: {pathPdf}", null, EventStatus.Failed);
            return;
        }

        var fileInfo = new FileInfo(pathPdf);
        _logger.LogInformation("Processing PDF: {Path}", pathPdf);

        if (fileInfo.Length == 0)
        {
            await ReportEventAsync(taskId, pathPdf, "Uploaded PDF is empty (0 bytes)", null, EventStatus.Failed);
            return;
        }

        int totalPages = 0;
        try
        {
            totalPages = ImageHelper.GetPageCount(pathPdf);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get page count from PDF: {Path}", pathPdf);
            await ReportEventAsync(taskId, pathPdf, $"Failed to read PDF: {ex.Message}", null, EventStatus.Failed);
            // Clean up temp file
            if (File.Exists(pathPdf))
            {
                try
                {
                    File.Delete(pathPdf);
                    _logger.LogInformation("Deleted temp file: {Path}", pathPdf);
                }
                catch (Exception)
                {
                    _logger.LogError("Failed to delete temp file: {Path}", pathPdf);
                }
            }
            return;
        }

        if (totalPages <= 0)
        {
            await ReportEventAsync(taskId, pathPdf, "PDF is empty or inaccessible", null, EventStatus.Failed);
            return;
        }

        try
        {
            await ReportEventAsync(taskId, pathPdf, "Job Started", null, EventStatus.Started);

            // Resolve OCR Engine ONCE
            var ocrEngine = _serviceProvider.GetKeyedService<IBaseOcrEngine>(modelEnum);
            if (ocrEngine == null)
            {
                await ReportEventAsync(taskId, pathPdf, $"OCR Engine for {modelKey} not found", null, EventStatus.Failed);
                return;
            }

            // 2. Pre-fetch tất cả page images song song (pipeline: render serialised, encode parallel)
            _logger.LogInformation("[JOB] Task {TaskId} — extracting {Total} page images (parallel pipeline)...", taskId, totalPages);
            ProcessedImage?[] pageImages;
            bool usePng = modelEnum == LlmSupport.DeepSeekOcr;
            try
            {
                pageImages = await ImageHelper.ProcessAllPdfPagesAsync(
                    pdfPath: pathPdf,
                    totalPages: totalPages,
                    targetDpi: 300,
                    minImageDim: 1536,
                    maxEncodeParallelism: 4,
                    usePng: usePng,
                    cancellationToken: token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JOB] Task {TaskId} — failed to extract page images.", taskId);
                throw;
            }

            var rotations = await FetchRotationsAsync(taskId, totalPages, pageImages, token);

            // 3. Initial Registration & Allocation
            var initialData = new
            {
                allowSlot = 0,
                used = 0,
                remainingPage = totalPages,
                TotalPage = totalPages
            };

            await _redisService.AllocateSlotsAsync(modelKey, taskId, totalMax, JsonSerializer.Serialize(initialData));

            // 5. Parallel OCR Processing with Distributed Throttle
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var jobToken = cts.Token;

            var pageTasks = Enumerable.Range(0, totalPages).Select(i =>
            {
                var img = pageImages[i];
                if (img == null)
                    throw new Exception($"Page image {i + 1} was not extracted successfully.");

                _logger.LogInformation("[ENQUEUE] Task {TaskId} page {Page}/{Total}", taskId, i + 1, totalPages);
                return ProcessPageAsync(taskId, pathPdf, ocrEngine, modelKey, i, totalPages, img, rotations[i], usePng, jobToken, cts);
            }).ToList();

            var resultsArray = await Task.WhenAll(pageTasks);
            var allPagesResult = resultsArray.ToList();

            var sb = new StringBuilder();
            for (int i = 0; i < allPagesResult.Count; i++)
            {
                sb.AppendLine(ocrEngine.ConvertPageToMarkdown(allPagesResult[i]));
                sb.AppendLine();
                sb.AppendLine($"Page {i + 1}");
                sb.AppendLine();
                sb.AppendLine("---");
            }
            string finalMarkdown = sb.ToString();


            stopwatch.Stop();
            var duration = stopwatch.Elapsed;

            // 5. Lưu kết quả
            await SaveMarkdownResultAsync(finalMarkdown, pathPdf, taskId);

            // Report Success
            await ReportEventAsync(taskId, pathPdf, "OCR Finished successfully", null, EventStatus.Successed, EventType.Logging, duration.TotalSeconds);

            // Post-success events: SaveLog and GetMarkdown
            await ReportEventAsync(taskId, pathPdf, "Logs Summary", null, EventStatus.Successed, EventType.SaveLog);
            await ReportEventAsync(taskId, pathPdf, "Markdown URL", null, EventStatus.Successed, EventType.GetMarkdown);
        }
        catch (OperationCanceledException)
        {
            await ReportEventAsync(taskId, pathPdf, "Job Canceled", null, EventStatus.Canceled);
        }
        catch (Exception ex)
        {
            await ReportEventAsync(taskId, pathPdf, $"Job Failed: {ex.Message}", null, EventStatus.Failed);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogInformation("--- [JOB FINISHED] Task {TaskId} Total Processing Time: {Duration} ---", taskId, stopwatch.Elapsed);
            await _redisService.RemoveWorkerAsync(modelKey, taskId);

            // Clean up temp file
            if (File.Exists(pathPdf))
            {
                try
                {
                    File.Delete(pathPdf);
                    _logger.LogInformation("Deleted temp file: {Path}", pathPdf);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete temp file: {Path}", pathPdf);
                }
            }
        }
    }

    private async Task<List<LayoutBlock>> ProcessPageAsync(
        string taskId,
        string pathPdf,
        IBaseOcrEngine ocrEngine,
        string modelKey,
        int pageIndex,
        int totalPages,
        ProcessedImage processedImage,
        int rotationDegrees,
        bool usePng,
        CancellationToken token,
        CancellationTokenSource jobCts)
    {
        // Wait for available concurrency slot
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await _redisService.IncrementUsedAsync(modelKey, taskId);
                if (result != null) break;
            }
            catch (Exception ex) when (ex.Message.Contains("Worker not found"))
            {
                _logger.LogWarning("Task {TaskId} worker was removed from Redis (likely canceled). Stopping loop.", taskId);
                jobCts.Cancel();
                throw new OperationCanceledException("Worker removed from Redis.");
            }

            await Task.Delay(300, token);
        }

        try
        {
            // Build the final ProcessedImage with rotation applied (if any).
            // All SKBitmap/native memory is disposed inside ApplyRotation before we enter the async API call.
            ProcessedImage finalImage = rotationDegrees != 0
                ? ApplyRotation(processedImage, rotationDegrees, taskId, pageIndex, totalPages, usePng)
                : processedImage;

            var ocrRequest = new OcrImageRequest
            {
                Image = finalImage,
                PageIndex = pageIndex,
                RotationDegrees = rotationDegrees
            };

            _logger.LogInformation(
                "[JOB] Task {TaskId} Page {Page}/{Total} — sending to OCR engine (rotation applied={Rotation}°, maxTokens={Tokens})",
                taskId, pageIndex + 1, totalPages, rotationDegrees, ocrRequest.MaxTokens);

            var sw = Stopwatch.StartNew();
            var pageResult = await ocrEngine.OcrImageAsync(ocrRequest, token);
            sw.Stop();

            int currentDone = Interlocked.Increment(ref _completedPages);

            await ReportEventAsync(taskId, pathPdf,
                $"Done {currentDone}/{totalPages} (Page {pageIndex + 1}) in {sw.Elapsed.TotalSeconds:F2}s",
                processingTime: sw.Elapsed.TotalSeconds);

            return pageResult ?? new List<LayoutBlock>();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "[JOB] Task {TaskId} Page {Page}/{Total} — canceled.",
                taskId, pageIndex + 1, totalPages);
            throw;
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "[JOB] Task {TaskId} Page {Page}/{Total} — failed. Cancelling entire job.",
                    taskId, pageIndex + 1, totalPages);
                jobCts.Cancel();
            }
            throw;
        }
        finally
        {
            await _redisService.DecrementUsedAsync(modelKey, taskId);
        }
    }

    /// <summary>
    /// Decodes the Base64 image, rotates it via SkiaSharp, re-encodes to PNG Base64, then
    /// disposes ALL bitmap objects before returning. By the time the caller enters the async
    /// OCR API call, no native (unmanaged) memory is held for the bitmap —
    /// only the compact Base64 string of <see cref="ProcessedImage"/> survives in heap.
    /// </summary>
    private ProcessedImage ApplyRotation(
        ProcessedImage source,
        int rotationDegrees,
        string taskId, int pageIndex, int totalPages,
        bool usePng)
    {
        _logger.LogInformation(
            "[JOB] Task {TaskId} Page {Page}/{Total} — rotating image by {Rotation}° before OCR.",
            taskId, pageIndex + 1, totalPages, rotationDegrees);

        // Wrap the byte[] in a MemoryStream so SKBitmap.Decode does not need a second copy.
        byte[] imgBytes = Convert.FromBase64String(source.Base64);
        using var ms = new System.IO.MemoryStream(imgBytes, writable: false);
        using var skBitmap = SkiaSharp.SKBitmap.Decode(ms);

        int correctionDegrees = -rotationDegrees;

        var rotated = ImageHelper.ProcessPdfPage(skBitmap, rotationDegrees: correctionDegrees, usePng: usePng);
        return rotated ?? source;   // fallback to original if rotate unexpectedly fails
    }

    private async Task<int[]> FetchRotationsAsync(
        string taskId,
        int totalPages,
        ProcessedImage?[] pageImages,
        CancellationToken token)
    {
        var rotations = new int[totalPages];
        var imagesToPredict = new List<(byte[] Bytes, string FileName)>();
        var pageMapping = new List<int>();

        for (int i = 0; i < totalPages; i++)
        {
            var img = pageImages[i];
            if (img == null || string.IsNullOrEmpty(img.Base64)) continue;

            imagesToPredict.Add((Convert.FromBase64String(img.Base64), $"page_{i + 1}.jpg"));
            pageMapping.Add(i);
        }

        if (imagesToPredict.Count == 0) return rotations;

        _logger.LogInformation(
            "[JOB] Task {TaskId} — sending all {Count} pages in a single orientation batch.",
            taskId, imagesToPredict.Count);

        try
        {
            var result = await _docOriService.PredictBatchAsync(imagesToPredict, token);

            for (int j = 0; j < imagesToPredict.Count && j < result.Predictions.Count; j++)
            {
                var prediction = result.Predictions[j];
                int pageIndex = pageMapping[j];

                if (int.TryParse(prediction.Orientation, out int parsedRot))
                    rotations[pageIndex] = parsedRot;

                _logger.LogDebug(
                    "[JOB] Task {TaskId} Page {Page} — orientation: {Rotation}° (confidence: {Conf:F2})",
                    taskId, pageIndex + 1, rotations[pageIndex], prediction.Confidence);
            }

            _logger.LogInformation("[JOB] Task {TaskId} — orientation batch processing done.", taskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JOB] Task {TaskId} — orientation batch failed. Stopping job as requested.", taskId);
            // Throwing OperationCanceledException will be caught by the main loop and reported as 'Canceled'
            throw new OperationCanceledException($"Orientation service unreachable or failed: {ex.Message}", ex);
        }

        return rotations;
    }


    /// <summary>
    /// Hàm thực hiện hai nhiệm vụ: Báo cáo event stream và quản lý log nội bộ.
    /// </summary>
    private async Task ReportEventAsync(
        string taskId,
        string filename,
        string message,
        string? data = null,
        EventStatus status = EventStatus.Processing,
        EventType type = EventType.Logging,
        double? processingTime = null)
    {
        // 1. Phép tính data đặc biệt dựa trên EventType
        string? finalData = data;

        if (type == EventType.SaveLog)
        {
            // Trình bày chuỗi JSON string của toàn bộ log đã append
            finalData = JsonSerializer.Serialize(_eventLogs, _jsonOptions);
        }
        else if (type == EventType.GetMarkdown)
        {
            // Trả về JSON string chứa url
            finalData = JsonSerializer.Serialize(new { Url = $"get-markdown/{taskId}" }, _jsonOptions);
        }

        // 2. Tạo đối tượng event
        var ocrEvent = new OcrEvent
        {
            TaskId = taskId,
            Filename = filename,
            Status = status,
            EventType = type,
            Message = message,
            DataJson = finalData,
            ProcessingTime = processingTime
        };

        // 3. Append log nội bộ (Chỉ dành cho type Logging)
        if (type == EventType.Logging)
        {
            _eventLogs.Add(new
            {
                TaskId = taskId,
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Message = message,
                Status = status.ToString()
            });
        }

        // 4. Phát stream (Hiện tại Log ra console, có thể mở rộng push vào Redis List/Stream)
        string eventJson = JsonSerializer.Serialize(ocrEvent, _jsonOptions);
        _logger.LogInformation("[EVENT_STREAM] task {taskId} status: {status}, message: {message}", taskId, status, message);

        await _redisService.PublishEventAsync($"ocr:stream:{taskId}", eventJson);
    }

    private async Task SaveMarkdownResultAsync(string content, string originalPdfPath, string taskId)
    {
        try
        {
            string outputDir = FileUtil.GetOutputDir();
            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

            string originalFileNameWithTaskId = Path.GetFileNameWithoutExtension(originalPdfPath);
            // Loại bỏ phần taskId prefix đã thêm ở Controller để tránh bị lặp lại
            string cleanFileName = originalFileNameWithTaskId.StartsWith(taskId)
                ? originalFileNameWithTaskId.Substring(taskId.Length).TrimStart('_')
                : originalFileNameWithTaskId;

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outputPath = Path.Combine(outputDir, $"{cleanFileName}_{taskId}_{timestamp}.md");

            await File.WriteAllTextAsync(outputPath, content, Encoding.UTF8);
            _logger.LogInformation("Saved markdown result to: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save markdown result");
        }
    }
}
