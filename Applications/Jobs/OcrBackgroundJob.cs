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
using System.Text.Encodings.Web;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;

namespace OCREngine.Applications.Jobs;


public class OcrBackgroundJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRedisService _redisService;
    private readonly ILogger<OcrBackgroundJob> _logger;
    private readonly LlmModelsOption _models;
    private readonly ExternalServiceOption _extOptions;
    private readonly IDocOriService _docOriService;
    private readonly IHostEnvironment _env;
    private static readonly JsonSerializerOptions _jsonOptionsCompact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions _jsonOptionsIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private int _completedPages = 0;
    private bool _isJobCancelled = false;
    private readonly object _jobLock = new();
    private readonly List<object> _eventLogs = new();

    /// <summary>
    /// Cache header/footer text từ các page đầu để lọc ở các page sau.
    /// Dùng ConcurrentDictionary để thread-safe khi OCR nhiều page song song.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _headerFooterTextCache = new(StringComparer.OrdinalIgnoreCase);
    private const int HeaderFooterLearnPages = 5; // Học từ 5 page đầu

    public OcrBackgroundJob(
        IServiceProvider serviceProvider,
        IRedisService redisService,
        ILogger<OcrBackgroundJob> logger,
        IOptions<LlmModelsOption> modelsOptions,
        IOptions<ExternalServiceOption> extOptions,
        IDocOriService docOriService,
        IHostEnvironment env)
    {
        _serviceProvider = serviceProvider;
        _redisService = redisService;
        _logger = logger;
        _models = modelsOptions.Value;
        _extOptions = extOptions.Value;
        _docOriService = docOriService;
        _env = env;
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

        // Create resource manager for automatic cleanup
        await using var resources = new OcrJobResources(taskId, pathPdf, _logger);

        // Step 1: Report job started
        await ReportEventAsync(taskId, pathPdf, "Job Started", null, EventStatus.Started);

        // Step 2: Resolve model configuration & concurrency settings
        var modelConfig = await ResolveModelConfigAsync(taskId, pathPdf, modelId);
        if (modelConfig == null) return;
        var (modelEnum, modelKey, totalMax) = modelConfig.Value;

        // Step 3: Resolve OCR engine from DI (early, needed for health check)
        var ocrEngine = await ResolveOcrEngineAsync(taskId, pathPdf, modelEnum, modelKey);
        if (ocrEngine == null) return;

        // Step 4: Health check — verify API reachable before expensive work
        if (!await ocrEngine.PingAsync(token))
        {
            await ReportEventAsync(taskId, pathPdf, "API health check failed: no healthy stream", null, EventStatus.Failed);
            await ReportEventAsync(taskId, pathPdf, "Logs Summary", null, EventStatus.Succeeded, EventType.SaveLog);
            return;
        }

        // Step 5: Validate PDF file and read total page count
        var totalPages = await ValidatePdfFileAsync(taskId, pathPdf);
        if (totalPages == null) return;

        // Step 6: Render PDF pages to local temp folder
        var renderedImagePaths = await RenderPdfPagesAsync(taskId, pathPdf, totalPages.Value);
        if (renderedImagePaths == null) return;

        // Step 7: Run OCR on all pages and save results
        string? finalModelKey = modelKey;
        try
        {
            await RunOcrOnAllPagesAsync(
                taskId, pathPdf,
                ocrEngine, modelKey, totalMax,
                totalPages.Value, renderedImagePaths,
                stopwatch, token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[JOB] Task {TaskId} — canceled by user or system shutdown.", taskId);
            await ReportEventAsync(taskId, pathPdf, "Job Canceled", null, EventStatus.Canceled);
            throw;
        }
        catch (Exception ex)
        {
            var baseEx = ex.GetBaseException();
            _logger.LogError(ex, "[JOB] Task {TaskId} — critical failure: {Message}", taskId, baseEx.Message);
            await ReportEventAsync(taskId, pathPdf, $"Job Failed: {baseEx.Message}", null, EventStatus.Failed);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogInformation("--- [JOB FINISHED] Task {TaskId} Total Processing Time: {Duration} ---", taskId, stopwatch.Elapsed);
            await _redisService.RemoveWorkerAsync(finalModelKey, taskId);
        }
    }

    /// <summary>
    /// Step 1: Validates modelId and retrieves the corresponding model configuration.
    /// Returns null if validation fails (event is already reported).
    /// </summary>
    private async Task<(LlmSupport modelEnum, string modelKey, int totalMax)?> ResolveModelConfigAsync(
        string taskId,
        string pathPdf,
        string modelId)
    {
        var modelEnumVal = LlmUtil.GetModelEnum(modelId);
        if (modelEnumVal == null)
        {
            await ReportEventAsync(taskId, pathPdf, "Invalid modelId", null, EventStatus.Failed);
            return null;
        }

        var modelEnum = modelEnumVal.Value;
        string modelKey = modelEnum.ToString();

        var modelOption = modelEnum switch
        {
            LlmSupport.DeepSeekOcr => _models.DeepSeek,
            LlmSupport.ChandraOcr => _models.Chandra,
            _ => null
        };

        if (modelOption == null)
        {
            await ReportEventAsync(taskId, pathPdf, $"Model configuration for {modelKey} not found", null, EventStatus.Failed);
            return null;
        }

        return (modelEnum, modelKey, modelOption.Concurrency);
    }

    /// <summary>
    /// Step 2: Validates that the PDF file exists, is non-empty, and returns its page count.
    /// Returns null if any validation check fails (event is already reported).
    /// </summary>
    private async Task<int?> ValidatePdfFileAsync(string taskId, string pathPdf)
    {
        if (!File.Exists(pathPdf))
        {
            await ReportEventAsync(taskId, pathPdf, $"File not found: {pathPdf}", null, EventStatus.Failed);
            return null;
        }

        var fileInfo = new FileInfo(pathPdf);
        _logger.LogInformation("Processing PDF: {Path}", pathPdf);

        if (fileInfo.Length == 0)
        {
            await ReportEventAsync(taskId, pathPdf, "Uploaded PDF is empty (0 bytes)", null, EventStatus.Failed);
            return null;
        }

        int totalPages;
        try
        {
            totalPages = await PdfHelper.GetPageCountAsync(pathPdf);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get page count from PDF: {Path}", pathPdf);
            await ReportEventAsync(taskId, pathPdf, $"Failed to read PDF: {ex.Message}", null, EventStatus.Failed);
            return null;
        }

        if (totalPages <= 0)
        {
            await ReportEventAsync(taskId, pathPdf, "PDF is empty or inaccessible", null, EventStatus.Failed);
            return null;
        }

        return totalPages;
    }

    /// <summary>
    /// Step 3: Renders all PDF pages to JPEG images in a local temp directory.
    /// Returns null if rendering fails (event is already reported).
    /// Throws OperationCanceledException if the job is canceled mid-render.
    /// </summary>
    private async Task<List<string>?> RenderPdfPagesAsync(string taskId, string pathPdf, int totalPages)
    {
        string tempImageDir = FileUtil.CreateJobTempDir(taskId);

        try
        {
            _logger.LogInformation(
                "[JOB] Task {TaskId} — rendering {TotalPages} pages to local tmp folder...",
                taskId, totalPages);

            var renderedImagePaths = await PdfHelper.RenderPdfToLocalAsync(
                pathPdf,
                tempImageDir,
                targetDpi: 200,
                format: "jpeg",
                quality: 100
                );

            _logger.LogInformation(
                "[JOB] Task {TaskId} — rendered {Count} pages to local tmp folder.",
                taskId, renderedImagePaths.Count);

            return renderedImagePaths;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[JOB] Task {TaskId} — canceled during PDF rendering.", taskId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JOB] Task {TaskId} — failed to render PDF pages.", taskId);
            await ReportEventAsync(taskId, pathPdf, $"Failed to render PDF: {ex.Message}", null, EventStatus.Failed);
            throw;
        }
    }

    /// <summary>
    /// Step 4: Resolves the keyed OCR engine service from the DI container.
    /// Returns null if the engine is not registered (event is already reported).
    /// </summary>
    private async Task<IBaseOcrEngine?> ResolveOcrEngineAsync(
        string taskId,
        string pathPdf,
        LlmSupport modelEnum,
        string modelKey)
    {
        var ocrEngine = _serviceProvider.GetKeyedService<IBaseOcrEngine>(modelEnum);
        if (ocrEngine == null)
        {
            await ReportEventAsync(taskId, pathPdf, $"OCR Engine for {modelKey} not found", null, EventStatus.Failed);
            return null;
        }

        return ocrEngine;
    }

    /// <summary>
    /// Step 5: Detects page orientations, allocates Redis concurrency slots,
    /// runs OCR on all pages in parallel, then persists and reports the results.
    /// </summary>
    private async Task RunOcrOnAllPagesAsync(
        string taskId,
        string pathPdf,
        IBaseOcrEngine ocrEngine,
        string modelKey,
        int totalMax,
        int totalPages,
        List<string> renderedImagePaths,
        Stopwatch stopwatch,
        CancellationToken token)
    {
        // 5a. Detect page orientations via thumbnails
        var rotations = await FetchRotationsAsync(taskId, totalPages, pathPdf, token);

        // 5b. Register task and allocate initial concurrency slots in Redis
        var initialData = new
        {
            allowSlot = 0,
            used = 0,
            remainingPage = totalPages,
            TotalPage = totalPages
        };
        await _redisService.AllocateSlotsAsync(modelKey, taskId, totalMax, JsonSerializer.Serialize(initialData));

        // 5c. Dispatch all pages for parallel OCR processing
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var jobToken = cts.Token;

        var pageTasks = Enumerable.Range(0, totalPages)
            .Select(i => ProcessPageAsync(
                taskId, renderedImagePaths[i],
                pathPdf,
                ocrEngine, modelKey,
                i, totalPages, rotations[i],
                jobToken, cts))
            .ToList();

        var allPagesResult = await Task.WhenAll(pageTasks);

        stopwatch.Stop();
        var duration = stopwatch.Elapsed;

        // 5d. Persist OCR results as JSON
        await SaveJsonResultAsync(allPagesResult.ToList(), pathPdf, taskId);

        // 5e. Report completion and trigger downstream events
        await ReportEventAsync(taskId, pathPdf, "OCR Finished successfully", null, EventStatus.Succeeded, EventType.Logging, duration.TotalSeconds);
        await ReportEventAsync(taskId, pathPdf, "Logs Summary", null, EventStatus.Succeeded, EventType.SaveLog);
        await ReportEventAsync(taskId, pathPdf, "JSON URL", null, EventStatus.Succeeded, EventType.GetMarkdown);
    }

    private async Task<PageOcrResult> ProcessPageAsync(
        string taskId,
        string imagePath,
        string pathPdf,
        IBaseOcrEngine ocrEngine,
        string modelKey,
        int pageIndex,
        int totalPages,
        int rotationDegrees,
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
                // _logger.LogWarning("Task {TaskId} worker was removed from Redis (likely canceled). Stopping loop.", taskId);
                jobCts.Cancel();
                throw new OperationCanceledException("Worker removed from Redis.");
            }

            await Task.Delay(500, token);
        }

        try
        {
            // Nếu có rotation, rotate trực tiếp từ file và lấy dimensions mới
            string base64;
            int width, height;

            if (rotationDegrees != 0)
            {
                // Rotate từ file → trả về Base64 + dimensions đã hoán đổi (nếu cần)
                base64 = ImageHelper.RotateImageToBase64(imagePath, -rotationDegrees);
                var rotatedBytes = Convert.FromBase64String(base64);
                (width, height) = ImageHelper.GetImageDimensions(rotatedBytes);
            }
            else
            {
                // Không rotate: dùng ảnh gốc
                var imageBytes = await File.ReadAllBytesAsync(imagePath, token);
                base64 = Convert.ToBase64String(imageBytes);
                (width, height) = ImageHelper.GetImageDimensions(imageBytes);
            }

            var ocrRequest = new OcrImageRequest
            {
                TaskId = taskId,
                Image = new ProcessedImage
                {
                    Base64 = base64,
                    ContentType = "image/jpeg",
                    Width = width,
                    Height = height
                },
                PageIndex = pageIndex,
                RotationDegrees = rotationDegrees
            };

            _logger.LogInformation(
                "[JOB] Task {TaskId} Page {Page}/{Total} — sending to OCR engine (rotation applied={Rotation}°, maxTokens={Tokens})",
                taskId, pageIndex + 1, totalPages, rotationDegrees, ocrRequest.MaxTokens);

            var sw = Stopwatch.StartNew();
            List<LayoutBlock> pageBlocks;

            try
            {
                pageBlocks = await ocrEngine.OcrImageAsync(ocrRequest, token);
            }
            catch (Exception)
            {
                sw.Stop();
                // _logger.LogError(ex,
                //     "[JOB] Task {TaskId} Page {Page}/{Total} — OCR engine failed after all retries (rotation={Rotation}°, elapsed={Elapsed}s)",
                //     taskId, pageIndex + 1, totalPages, rotationDegrees, sw.Elapsed.TotalSeconds);
                throw;
            }

            // Convert to PageOcrResult
            var sw2 = Stopwatch.StartNew();

            // Học header/footer từ các page đầu, lọc ở các page sau
            if (pageIndex < HeaderFooterLearnPages)
            {
                // Thu thập header/footer text từ các page đầu
                CollectHeaderFooterText(pageBlocks);
            }
            else if (pageIndex >= HeaderFooterLearnPages)
            {
                // Loại bỏ header/footer ở page 6+ (theo category hoặc trùng text cache)
                pageBlocks = FilterHeaderFooterBlocks(pageBlocks);
            }

            var pageResult = await ocrEngine.ConvertPageToMarkdownAsync(
                pageBlocks, base64, pageIndex, includeHeaderFooter: pageIndex < 3);
            sw2.Stop();
            sw.Stop();

            int currentDone = Interlocked.Increment(ref _completedPages);

            _logger.LogInformation(
                "[JOB] Task {TaskId} Page {Page}/{Total} — completed in {Elapsed}s (progress={Done}/{Total})",
                taskId, pageIndex + 1, totalPages, sw.Elapsed.TotalSeconds, currentDone, totalPages);

            await ReportEventAsync(taskId, pathPdf,
                $"Done {currentDone}/{totalPages} (Page {pageIndex + 1}) in {sw.Elapsed.TotalSeconds:F2}s",
                processingTime: sw.Elapsed.TotalSeconds);

            return pageResult;
        }
        catch (OperationCanceledException)
        {
            // if (!_isJobCancelled)
            // {
            //     _logger.LogDebug("[JOB] Task {TaskId} Page {Page}/{Total} — canceled by user or system shutdown.",
            //         taskId, pageIndex + 1, totalPages);
            // }
            throw;
        }
        catch (Exception)
        {
            lock (_jobLock)
            {
                if (!_isJobCancelled && !token.IsCancellationRequested)
                {
                    _isJobCancelled = true;
                    // _logger.LogError(ex,
                    //     "[JOB] Task {TaskId} Page {Page}/{Total} — failed. Cancelling entire job.",
                    //     taskId, pageIndex + 1, totalPages);
                    jobCts.Cancel();
                }
            }
            throw;
        }
        finally
        {
            await _redisService.DecrementUsedAsync(modelKey, taskId);
        }
    }

    private async Task<int[]> FetchRotationsAsync(
        string taskId,
        int totalPages,
        string pdfPath,
        CancellationToken token)
    {
        var rotations = new int[totalPages];

        _logger.LogInformation(
            "[JOB] Task {TaskId} — generating orientation thumbnails for {TotalPages} pages...",
            taskId, totalPages);

        // Render tất cả thumbnails 1 lần
        var thumbnailBytesList = await PdfHelper.RenderAllThumbnailsAsync(pdfPath, batchSize: 10);

        // Chuẩn bị data cho prediction
        var imagesToPredict = thumbnailBytesList
            .Select((bytes, idx) => (Bytes: bytes, FileName: $"page_{idx + 1}.jpg"))
            .ToList();

        try
        {
            _logger.LogInformation("[JOB] Task {TaskId} — sending all {Count} pages for orientation prediction...", taskId, totalPages);

            var result = await _docOriService.PredictBatchAsync(imagesToPredict, token);

            for (int i = 0; i < result.Predictions.Count && i < totalPages; i++)
            {
                var prediction = result.Predictions[i];
                int rawRot = 0;

                if (int.TryParse(prediction.Orientation, out int parsedRot))
                {
                    rawRot = parsedRot;
                    // Nếu Confidence < 0.7 thì ép về 0 độ (Default)
                    rotations[i] = (prediction.Confidence >= 0.7) ? parsedRot : 0;
                }

                _logger.LogDebug(
                    "[JOB] Task {TaskId} Page {Page}: Raw={RawRot}°, Applied={AppliedRot}° (Confidence={Conf:F2})",
                    taskId, i + 1, rawRot, rotations[i], prediction.Confidence);
            }

            _logger.LogInformation("[JOB] Task {TaskId} — orientation detection complete. Total={Total}, Rotated={Count}",
                taskId, totalPages, rotations.Count(r => r != 0));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JOB] Task {TaskId} — orientation prediction failed.", taskId);
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
        object? extraData = null,
        EventStatus status = EventStatus.Processing,
        EventType type = EventType.Logging,
        double? processingTime = null)
    {
        // 1. Phép tính data đặc biệt dựa trên EventType
        string? finalData = null;

        if (extraData is string dataStr)
        {
            finalData = dataStr;
        }

        if (type == EventType.SaveLog)
        {
            // Trình bày chuỗi JSON string của toàn bộ log đã append
            finalData = JsonSerializer.Serialize(_eventLogs, _jsonOptionsCompact);
        }
        else if (type == EventType.GetMarkdown)
        {
            // Trả về JSON string chứa url và danh sách ảnh (nếu có)
            finalData = JsonSerializer.Serialize(new
            {
                Url = $"get-markdown/{taskId}"
            }, _jsonOptionsCompact);
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
        _logger.LogDebug("[EVENT_STREAM] task {taskId} status: {status}, message: {message}", taskId, status, message);
        await _redisService.PublishEventAsync("ocr:events:stream", ocrEvent);
    }

    private async Task SaveJsonResultAsync(List<PageOcrResult> pages, string originalPdfPath, string taskId)
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

            // Không dùng timestamp, chỉ cần taskId là đủ unique
            string outputPath = Path.Combine(outputDir, $"{cleanFileName}_{taskId}.json");

            var jsonContent = JsonSerializer.Serialize(pages, _jsonOptionsIndented);
            await File.WriteAllTextAsync(outputPath, jsonContent, Encoding.UTF8);
            _logger.LogInformation("Saved JSON result to: {Path}", outputPath);

            // Save cropped images to debug folder (only in Development environment)
            if (_env.IsDevelopment())
            {
                try
                {
                    foreach (var page in pages)
                    {
                        if (page.Images != null && page.Images.Count > 0)
                        {
                            foreach (var kvp in page.Images)
                            {
                                string imageKey = kvp.Key;
                                string localFileName = Path.GetFileName(imageKey);
                                // Thêm page index để tránh trùng lặp tên file
                                string debugFileName = $"p{page.PageIndex}_{localFileName}";
                                await FileUtil.SaveDebugImageAsync(kvp.Value, taskId, debugFileName);
                            }
                        }
                    }
                    _logger.LogInformation("Saved all cropped images to debug folder for task {TaskId}", taskId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save cropped images to debug folder");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save JSON result");
            throw;
        }
    }

    #region Header/Footer Filtering

    /// <summary>
    /// Thu thập text từ các block PageHeader/PageFooter vào cache.
    /// </summary>
    private void CollectHeaderFooterText(List<LayoutBlock> blocks)
    {
        if (blocks == null) return;

        foreach (var block in blocks)
        {
            if (block.Category is LayoutCategory.PageHeader or LayoutCategory.PageFooter)
            {
                var text = ExtractPlainText(block);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _headerFooterTextCache.TryAdd(text.Trim(), byte.MinValue);
                }
            }
        }
    }

    /// <summary>
    /// Loại bỏ các block header/footer khỏi danh sách blocks.
    /// Lọc theo category HOẶC text trùng với cache đã học.
    /// </summary>
    private List<LayoutBlock> FilterHeaderFooterBlocks(List<LayoutBlock> blocks)
    {
        if (blocks == null || blocks.Count == 0)
            return blocks ?? new List<LayoutBlock>();

        var headerFooterCategories = new[] { LayoutCategory.PageHeader, LayoutCategory.PageFooter };

        return blocks.Where(block =>
        {
            // Loại bỏ theo category
            if (headerFooterCategories.Any(c => c == block.Category))
                return false;

            // Loại bỏ theo text cache
            var text = ExtractPlainText(block);
            if (!string.IsNullOrWhiteSpace(text) && _headerFooterTextCache.ContainsKey(text.Trim()))
            {
                _logger.LogDebug("[JOB] Filtered block matching cached header/footer text: '{Text}'", text.Trim());
                return false;
            }

            return true;
        }).ToList();
    }

    /// <summary>
    /// Trích xuất plain text từ LayoutBlock (hỗ trợ HTML từ Chandra).
    /// </summary>
    private static string ExtractPlainText(LayoutBlock block)
    {
        if (string.IsNullOrEmpty(block.Text))
            return string.Empty;

        // Nếu là HTML (Chandra), strip tags
        if (block.Text.Contains('<'))
        {
            try
            {
                return System.Text.RegularExpressions.Regex.Replace(block.Text, "<[^>]+>", string.Empty).Trim();
            }
            catch
            {
                return block.Text;
            }
        }

        return block.Text;
    }

    #endregion
}
