using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Azure.AI.Inference;
using OCREngine.Factories;
using OCREngine.Models;
using OCREngine.Helpers;
using OCREngine.Models.Enum;

namespace OCREngine.Applications.Interfaces;

public interface IBaseOcrEngine
{
    /// <summary>
    /// Perform OCR on a single pre-processed image. PDF extraction and rotation detection
    /// must be done by the caller (Job layer) before calling this method.
    /// </summary>
    Task<List<LayoutBlock>> OcrImageAsync(OcrImageRequest request, CancellationToken cancellationToken);
    string ConvertPageToMarkdown(List<LayoutBlock> page, bool includeHeaderFooter = false);
    Task TransformBboxImageToBase64Async(string base64Image, List<LayoutBlock> blocks);
}

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public abstract class BaseOcrEngine : IBaseOcrEngine
{
    protected readonly ChatCompletionsClient _chatClient;
    protected readonly string _modelName;
    protected const int MAX_RETRY = 3;
    protected readonly ILogger<BaseOcrEngine> _logger;

    protected BaseOcrEngine(LlmSupport llmSupport, OpenAiClientFactory openAiClientFactory, ILogger<BaseOcrEngine> logger)
    {
        _chatClient = openAiClientFactory.CreateChatClient(llmSupport);
        _modelName = openAiClientFactory.GetModelName(llmSupport);
        _logger = logger;
    }

    /// <summary>
    /// Perform OCR on the given image. Override this in derived classes to call the specific LLM API.
    /// </summary>
    protected abstract Task<OcrResponse> OcrImageCoreAsync(OcrImageRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Public entry point for Engine layer.
    /// Handles retry logic for LLM-level errors (length limit, repetition, empty content).
    /// Parse/JSON errors are NOT retried — they are thrown immediately.
    /// PDF extraction and DocOri rotation must be resolved by the caller before invoking this.
    /// </summary>
    public async Task<List<LayoutBlock>> OcrImageAsync(OcrImageRequest request, CancellationToken cancellationToken)
    {
        if (request?.Image == null || string.IsNullOrEmpty(request.Image.Base64))
            throw new ArgumentException("OcrImageRequest must contain a valid ProcessedImage with Base64 data.");

        // Work on a mutable copy so the caller's object is not modified between retries
        var currentRequest = new OcrImageRequest
        {
            Image = request.Image,
            MaxTokens = request.MaxTokens,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            PageIndex = request.PageIndex,   // ← forward page index để dùng trong log/save
        };

        int attempt = 0;
        string? lastFailedContent = null;  // lưu content cuối cùng bị repetition/length để debug

        while (attempt < MAX_RETRY)
        {
            attempt++;
            try
            {
                // Lưu ảnh lần đầu để kiểm tra chất lượng pipeline
                // if (attempt == 1)
                //     await SaveProcessedImageAsync(request.PageIndex, request.Image);

                var response = await OcrImageCoreAsync(currentRequest, cancellationToken);

                bool isRepetitive = IsRepetitive(response.Content, 500);
                bool isLengthLimit = response.FinishReason?.Equals("length", StringComparison.OrdinalIgnoreCase) ?? false;

                if (isLengthLimit || isRepetitive)
                {
                    string reason = isRepetitive ? "REPETITION" : "LENGTH";

                    // Lưu lại nội dung bị lỗi để debug
                    await SaveRawResponseAsync(request.PageIndex, response.Content, attempt, reason);

                    if (response.TokenCount > 10000 && !isRepetitive)
                    {
                        _logger.LogError(
                            "[ENGINE][ABORT] Generated {Tokens} tokens but hit length limit — likely a runaway loop. Aborting retries.",
                            response.TokenCount);
                        throw new Exception($"Runaway loop detected: {response.TokenCount} tokens generated without finishing.");
                    }

                    _logger.LogWarning(
                        "[ENGINE][RETRY] Attempt {Attempt}/{Max} — hit {Reason} ({Tokens} tokens). Increasing tokens and penalty. PageIndex: {PageIndex}",
                        attempt, MAX_RETRY, reason, response.TokenCount, request.PageIndex);

                    // Luôn lưu lại content mới nhất để log khi hết retry
                    lastFailedContent = response.Content;

                    if (attempt >= MAX_RETRY) break;

                    currentRequest.MaxTokens += 2000;
                    currentRequest.FrequencyPenalty = (currentRequest.FrequencyPenalty ?? 0) + 0.2f;
                    currentRequest.PresencePenalty = (currentRequest.PresencePenalty ?? 0) + 0.2f;
                    continue;
                }

                // Clean trước (loại bỏ token đặc biệt như <｜end▁of▁sentence｜>)
                string content = CleanRawResponse(response.Content);

                // Kiểm tra empty SAU KHI clean — raw có thể chỉ chứa token đặc biệt
                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning(
                        "[ENGINE][EMPTY] Page {Page} — Attempt {Attempt}/{Max} — content EMPTY after clean " +
                        "(FinishReason={Reason}, Tokens={Tokens}, RawLen={RawLen}).",
                        request.PageIndex + 1, attempt, MAX_RETRY,
                        response.FinishReason, response.TokenCount,
                        response.Content?.Length ?? 0);
                    if (attempt < MAX_RETRY)
                    {
                        // Reset về default — không dùng penalty cho trường hợp empty
                        currentRequest.FrequencyPenalty = null;
                        currentRequest.PresencePenalty = null;
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }
                    // Trang trắng hợp lệ — trả về list rỗng thay vì throw
                    _logger.LogWarning(
                        "[ENGINE][BLANK_PAGE] Page {Page} — content still empty after {Max} retries. Treating as blank page.",
                        request.PageIndex + 1, MAX_RETRY);
                    return new List<LayoutBlock>();
                }

                // Parse errors are NOT retried — throw immediately so the caller can handle appropriately.
                List<LayoutBlock> listBlock = await ParseResponseToLayoutBlocksAsync(content, request.Image);

                var hasPictures = listBlock.Any(b =>
                    b.Category == LayoutCategory.Picture ||
                    b.Category == LayoutCategory.Image ||
                    b.Category == LayoutCategory.Figure);

                if (hasPictures)
                {
                    _logger.LogDebug("[ENGINE] Processing crop for Picture/Image/Figure blocks...");
                    await TransformBboxImageToBase64Async(request.Image.Base64, listBlock);
                }

                _logger.LogDebug("[ENGINE] OCR completed successfully on attempt {Attempt}.", attempt);
                return listBlock;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                // Parse errors: do NOT retry — output will likely be the same on retry with the same input.
                _logger.LogError(ex, "[ENGINE][PARSE_ERROR] JSON parsing failed. Not retrying.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[ENGINE][RETRY] Attempt {Attempt}/{Max} — error during API call.",
                    attempt, MAX_RETRY);
                if (attempt >= MAX_RETRY) throw;

                await Task.Delay(3000, cancellationToken);
            }
        }

        // Log truncated content để debug repetition sau khi đã hết toàn bộ retry
        if (!string.IsNullOrEmpty(lastFailedContent))
        {
            const int HEAD_LEN = 500;
            const int TAIL_LEN = 500;

            string head = lastFailedContent.Length > HEAD_LEN
                ? lastFailedContent[..HEAD_LEN]
                : lastFailedContent;

            string tail = lastFailedContent.Length > TAIL_LEN
                ? lastFailedContent[^TAIL_LEN..]
                : string.Empty;

            _logger.LogError(
                "[ENGINE][REPETITION] All {Max} attempts exhausted. Content length={Len} chars.\n" +
                "--- HEAD (first {HeadLen} chars) ---\n{Head}\n" +
                "--- TAIL (last {TailLen} chars) ---\n{Tail}",
                MAX_RETRY, lastFailedContent.Length,
                HEAD_LEN, head,
                TAIL_LEN, tail);
        }

        throw new Exception($"OCR engine failed after {MAX_RETRY} attempts.");
    }

    /// <summary>
    /// Parses the OCR response content into a list of LayoutBlocks. Override if the response is not JSON.
    /// </summary>
    protected virtual Task<List<LayoutBlock>> ParseResponseToLayoutBlocksAsync(string content, ProcessedImage image)
    {
        try
        {
            var listBlock = JsonSerializer.Deserialize<List<LayoutBlock>>(content) ?? new List<LayoutBlock>();
            return Task.FromResult(listBlock);
        }
        catch (JsonException)
        {
            // Truncate to avoid log spam
            string preview = content.Length > 2000 ? content[..2000] + "...[truncated]" : content;
            _logger.LogError("JSON deserialization failed. Raw cleaned content:\n{RawContent}", preview);
            throw;
        }
    }

    /// <summary>
    /// Converts a single page's layout blocks to a markdown string.
    /// Override in derived classes for model-specific formatting (e.g. Dots, Chandra).
    /// Default: join each block's Text with newlines.
    /// </summary>
    public virtual string ConvertPageToMarkdown(List<LayoutBlock> page, bool includeHeaderFooter = false)
        => string.Join($"{Environment.NewLine}{Environment.NewLine}", page.Select(b => b.Text ?? string.Empty));

    public virtual async Task TransformBboxImageToBase64Async(string base64Image, List<LayoutBlock> blocks)
    {
        if (blocks == null || string.IsNullOrEmpty(base64Image)) return;

        var pictureBlocks = blocks.Where(b =>
            (b.Category == LayoutCategory.Picture || b.Category == LayoutCategory.Image || b.Category == LayoutCategory.Figure)
            && b.Bbox != null && b.Bbox.Count == 4).ToList();

        _logger.LogDebug("[ENGINE] Processing {Count} picture blocks for cropping.", pictureBlocks.Count);

        var tasks = pictureBlocks.Select(block => Task.Run(() =>
        {
            try
            {
                string croppedBase64 = ImageHelper.CropImageToBase64(
                    base64Image,
                    (int)block.Bbox![0],
                    (int)block.Bbox![1],
                    (int)block.Bbox![2],
                    (int)block.Bbox![3]
                );

                if (!string.IsNullOrEmpty(croppedBase64))
                {
                    block.Text = $"![image](data:image/png;base64,{croppedBase64})";
                    _logger.LogDebug("[ENGINE] Cropped block at [{x1},{y1},{x2},{y2}]",
                        block.Bbox[0], block.Bbox[1], block.Bbox[2], block.Bbox[3]);
                }
                else
                {
                    _logger.LogWarning("[ENGINE] CropImageToBase64 returned empty for a picture block.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ENGINE] Failed to crop picture block.");
            }
        }));

        await Task.WhenAll(tasks);
    }

    public abstract ChatRequestMessage CreateMessage(string prompt, string base64Image);

    /// <summary>
    /// Detects if the content contains a large sequence of repeated text, indicating a model loop.
    /// </summary>
    protected bool IsRepetitive(string content, int windowSize = 500)
    {
        if (string.IsNullOrEmpty(content) || content.Length < windowSize * 2)
            return false;

        string lastWindow = content.Substring(content.Length - windowSize);
        int searchRange = Math.Min(content.Length - windowSize, 2000);
        string searchSpace = content.Substring(content.Length - windowSize - searchRange, searchRange);

        return searchSpace.Contains(lastWindow);
    }

    /// <summary>
    /// Cleans raw response string before parsing. Implement model-specific logic in derived classes.
    /// </summary>
    protected abstract string CleanRawResponse(string text);

    /// <summary>
    /// Lưu raw response text ra file trong Outputs/RawResponses/ để debug.
    /// Tên file: raw_page_{pageIndex+1}_attempt_{attempt}_{timestamp}.txt
    /// </summary>
    private async Task SaveRawResponseAsync(int pageIndex, string content, int attempt, string status)
    {
        try
        {
            string outputDir = Path.Combine(Directory.GetCurrentDirectory(), "Outputs", "RawResponses");
            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outputPath = Path.Combine(outputDir,
                $"raw_page_{pageIndex + 1}_attempt_{attempt}_{status}_{timestamp}.txt");

            await File.WriteAllTextAsync(outputPath, content, Encoding.UTF8);
            _logger.LogDebug("[ENGINE] Saved raw response: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ENGINE] Failed to save raw response for page {Page}.", pageIndex + 1);
        }
    }
    /// <summary>
    /// Lưu processed image (PNG) vào Outputs/ProcessedImages/ để kiểm tra chất lượng pipeline.
    /// Tên file: page_{pageIndex+1}_{Width}x{Height}_{timestamp}.png
    /// Chỉ gọi ở attempt=1 để tiết kiệm disk.
    /// </summary>
    private async Task SaveProcessedImageAsync(int pageIndex, ProcessedImage image)
    {
        try
        {
            string outputDir = Path.Combine(Directory.GetCurrentDirectory(), "Outputs", "ProcessedImages");
            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outputPath = Path.Combine(outputDir,
                $"page_{pageIndex + 1}_{image.Width}x{image.Height}_{timestamp}.png");

            byte[] pngBytes = Convert.FromBase64String(image.Base64);
            await File.WriteAllBytesAsync(outputPath, pngBytes);
            _logger.LogDebug("[ENGINE] Saved processed image: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ENGINE] Failed to save processed image for page {Page}.", pageIndex + 1);
        }
    }
}