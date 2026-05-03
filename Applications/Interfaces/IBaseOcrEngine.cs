using System.Text.Json;
using System.Collections.Concurrent;
using System.Net.Http;
using Azure.AI.Inference;
using OCREngine.Factories;
using OCREngine.Models;
using OCREngine.Helpers;
using OCREngine.Models.Enum;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using OCREngine.Utils;

namespace OCREngine.Applications.Interfaces;

public interface IBaseOcrEngine
{
    /// <summary>
    /// Perform OCR on a single pre-processed image. PDF extraction and rotation detection
    /// must be done by the caller (Job layer) before calling this method.
    /// </summary>
    Task<List<LayoutBlock>> OcrImageAsync(OcrImageRequest request, CancellationToken cancellationToken);
    /// <summary>
    /// Converts layout blocks to markdown result with cropped images.
    /// </summary>
    /// <param name="page">Layout blocks from OCR</param>
    /// <param name="base64Image">Original page image for cropping</param>
    /// <param name="pageIndex">Page index</param>
    /// <param name="includeHeaderFooter">Include header/footer blocks</param>
    /// <param name="contentType">Content type for image references</param>
    Task<PageOcrResult> ConvertPageToMarkdownAsync(List<LayoutBlock> page, string? base64Image, int pageIndex, bool includeHeaderFooter = false, string? contentType = null);
    Task<Dictionary<string, string>> TransformBboxImageToBase64Async(string? base64Image, List<LayoutBlock> blocks, string? contentType = null);
    Task<bool> PingAsync(CancellationToken cancellationToken);
}


public abstract class BaseOcrEngine : IBaseOcrEngine
{
    protected readonly ChatCompletionsClient? _chatClient;
    protected readonly string _modelName;
    protected const int MAX_RETRY = 2;
    protected readonly ILogger<BaseOcrEngine> _logger;
    protected readonly bool _isDevelopment;
    protected readonly string? _baseUrl;
    protected readonly string? _apiKey;

    protected BaseOcrEngine(LlmSupport llmSupport, OpenAiClientFactory openAiClientFactory, ILogger<BaseOcrEngine> logger, IWebHostEnvironment env)
    {
        _chatClient = openAiClientFactory.CreateChatClient(llmSupport);
        _modelName = openAiClientFactory.GetModelName(llmSupport);
        _baseUrl = openAiClientFactory.GetBaseUrl(llmSupport);
        _apiKey = openAiClientFactory.GetApiKey(llmSupport);
        _logger = logger;
        _isDevelopment = env.IsDevelopment();
    }

    protected BaseOcrEngine(LlmSupport llmSupport, ILogger<BaseOcrEngine> logger, IWebHostEnvironment env)
    {
        _modelName = llmSupport.ToString();
        _logger = logger;
        _isDevelopment = env.IsDevelopment();
        _baseUrl = null;
        _apiKey = null;
    }

    /// <summary>
    /// Perform OCR on the given image. Override this in derived classes to call the specific LLM API.
    /// </summary>
    protected abstract Task<OcrResponse> OcrImageCoreAsync(OcrImageRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Lưu raw response của model trong môi trường dev để debug.
    /// </summary>
    protected async Task SaveRawResponseAsync(string rawContent, string taskId, int pageIndex, string model, DateTime timestamp, int tokenCount = 0)
    {
        if (!_isDevelopment)
            return;

        try
        {
            var filePath = await FileUtil.SaveRawModelResponseAsync(
                rawContent,
                taskId,
                pageIndex,
                model,
                tokenCount,
                timestamp);
            
            if (filePath != null)
                _logger.LogDebug("[DEV] Saved raw response to {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEV] Failed to save raw response");
        }
    }

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

        var currentRequest = new OcrImageRequest
        {
            TaskId = request.TaskId,
            Image = request.Image,
            MaxTokens = request.MaxTokens,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            PageIndex = request.PageIndex,
        };

        int attempt = 0;
        string? lastFailedContent = null;

        while (attempt < MAX_RETRY)
        {
            attempt++;
            try
            {
                var response = await OcrImageCoreAsync(currentRequest, cancellationToken);

                // Save raw response trong môi trường dev
                await SaveRawResponseAsync(
                    response.Content,
                    request.TaskId,
                    request.PageIndex,
                    _modelName,
                    DateTime.UtcNow,
                    response.TokenCount);

                bool isRepetitive = IsRepetitive(response.Content, 500);
                bool isLengthLimit = response.FinishReason?.Equals("length", StringComparison.OrdinalIgnoreCase) ?? false;

                if (isLengthLimit || isRepetitive)
                {
                    string reason = isRepetitive ? "REPETITION" : "LENGTH";

                    if (response.TokenCount > 10000 && !isRepetitive)
                    {
                        _logger.LogWarning(
                            "[ENGINE][Task:{TaskId}] Generated {Tokens} tokens but hit length limit — likely a runaway loop.",
                            request.TaskId, response.TokenCount);
                        throw new Exception($"Runaway loop detected: {response.TokenCount} tokens generated without finishing.");
                    }

                    _logger.LogWarning(
                        "[ENGINE][Task:{TaskId}] Page {Page} — Attempt {Attempt}/{Max} — hit {Reason} ({Tokens} tokens). Increasing tokens and penalty.",
                        request.TaskId, request.PageIndex + 1, attempt, MAX_RETRY, reason, response.TokenCount);

                    lastFailedContent = response.Content;

                    if (attempt >= MAX_RETRY) break;

                    currentRequest.MaxTokens += 1000;
                    currentRequest.FrequencyPenalty = (currentRequest.FrequencyPenalty ?? 0) + 0.2f;
                    currentRequest.PresencePenalty = (currentRequest.PresencePenalty ?? 0) + 0.2f;
                    continue;
                }

                string content = CleanRawResponse(response.Content);

                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning(
                        "[ENGINE][Task:{TaskId}] Page {Page} — Attempt {Attempt}/{Max} — content empty after clean (FinishReason={Reason}, Tokens={Tokens}).",
                        request.TaskId, request.PageIndex + 1, attempt, MAX_RETRY, response.FinishReason, response.TokenCount);

                    if (attempt < MAX_RETRY)
                    {
                        currentRequest.FrequencyPenalty = null;
                        currentRequest.PresencePenalty = null;
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }

                    _logger.LogWarning(
                        "[ENGINE][Task:{TaskId}] Page {Page} — content still empty after {Max} retries. Treating as blank page.",
                        request.TaskId, request.PageIndex + 1, MAX_RETRY);
                    return new List<LayoutBlock>();
                }

                List<LayoutBlock> listBlock = await ParseResponseToLayoutBlocksAsync(content, request);

                if (attempt > 1)
                {
                    _logger.LogWarning(
                        "[ENGINE][Task:{TaskId}] Page {Page} — OCR succeeded on attempt {Attempt}/{Max}.",
                        request.TaskId, request.PageIndex + 1, attempt, MAX_RETRY);
                }

                return listBlock;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // error network auto throw, do not retry
            catch (Azure.RequestFailedException ex)
            {
                if (cancellationToken.IsCancellationRequested) throw;

                if (ex.Status == 404 || ex.Status >= 500)
                {
                    _logger.LogError(ex,
                        "[ENGINE][Task:{TaskId}] {Status} error (Page {PageIndex}) — aborting retry.",
                        request.TaskId, ex.Status, request.PageIndex);
                }

                _logger.LogDebug(ex,
                    "[ENGINE][Task:{TaskId}] Attempt {Attempt}/{Max} — API call failed (Page {PageIndex}). Error: {Error}",
                    request.TaskId, attempt, MAX_RETRY, request.PageIndex, ex.Message);

                throw;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex,
                    "[ENGINE][Task:{TaskId}] JSON parsing failed (Page {PageIndex}). Message: {Message}",
                    request.TaskId, request.PageIndex, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested) throw;

                _logger.LogDebug(ex,
                    "[ENGINE][Task:{TaskId}] Attempt {Attempt}/{Max} — error during OCR (Page {PageIndex}). Error: {Error}",
                    request.TaskId, attempt, MAX_RETRY, request.PageIndex, ex.Message);

                if (attempt >= MAX_RETRY) throw;

                await Task.Delay(3000, cancellationToken);
            }
        }

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
                "[ENGINE][Task:{TaskId}] All {Max} attempts exhausted for Page {Page}. Last error content (HEAD/TAIL):\n{Head}\n...\n{Tail}",
                request.TaskId, MAX_RETRY, request.PageIndex + 1, head, tail);
        }

        throw new Exception($"OCR engine failed after {MAX_RETRY} attempts.");
    }

    /// <summary>
    /// Parses the OCR response content into a list of LayoutBlocks. Override if the response is not JSON.
    /// this is option default for model Dotsocr
    /// </summary>
    protected virtual Task<List<LayoutBlock>> ParseResponseToLayoutBlocksAsync(string content, OcrImageRequest request)
    {
        try
        {
            var listBlock = JsonSerializer.Deserialize<List<LayoutBlock>>(content) ?? new List<LayoutBlock>();
            return Task.FromResult(listBlock);
        }
        catch (JsonException)
        {
            throw;
        }
    }

    /// <summary>
    /// Converts a single page's layout blocks to a PageOcrResult containing markdown and cropped images.
    /// Default: join each block's Text with newlines.
    /// Crop của Picture/Image/Figure blocks và Image extraction từ Table/Text được thực hiện ở đây.
    /// </summary>
    public virtual async Task<PageOcrResult> ConvertPageToMarkdownAsync(List<LayoutBlock> page, string? base64Image, int pageIndex, bool includeHeaderFooter = false, string? contentType = null)
    {
        var images = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(base64Image))
        {
            var hasPictures = page.Any(b =>
                b.Category == LayoutCategory.Picture ||
                b.Category == LayoutCategory.Image ||
                b.Category == LayoutCategory.Figure);

            if (hasPictures)
            {
                var croppedPictures = await TransformBboxImageToBase64Async(base64Image, page, contentType);
                foreach (var kvp in croppedPictures) images[kvp.Key] = kvp.Value;
            }

            // 2. Xử lý extract ảnh từ Table block (chỉ các model hỗ trợ mới override)
            var tableImages = await ExtractImagesFromTableBlocksAsync(page, base64Image);
            foreach (var kvp in tableImages)
            {
                images[kvp.Key] = kvp.Value;
            }
        }

        var result = new PageOcrResult
        {
            PageIndex = pageIndex,
            Markdown = string.Join($"{Environment.NewLine}{Environment.NewLine}", page.Select(b => b.Text ?? string.Empty)),
            Images = images
        };
        return result;
    }

    /// <summary>
    /// Extract images từ Table blocks.
    /// Mỗi model OCR sẽ override method này để parse text và extract ảnh theo cách riêng.
    /// </summary>
    /// <param name="page">Danh sách layout blocks</param>
    /// <param name="base64Image">Ảnh trang gốc dạng base64 để crop</param>
    /// <returns>Dictionary: key là src ảnh (bbox/path), value là base64 ảnh đã crop</returns>
    public virtual Task<Dictionary<string, string>> ExtractImagesFromTableBlocksAsync(List<LayoutBlock> page, string base64Image)
    {
        // Default: không làm gì, trả về empty dictionary
        // Các model OCR hỗ trợ table image extraction sẽ override method này
        return Task.FromResult(new Dictionary<string, string>());
    }

    public virtual async Task<Dictionary<string, string>> TransformBboxImageToBase64Async(string? base64Image, List<LayoutBlock> blocks, string? contentType = null)
    {
        var images = new ConcurrentDictionary<string, string>();

        if (blocks == null || string.IsNullOrEmpty(base64Image))
            return new Dictionary<string, string>();

        var picCategories = new[] { LayoutCategory.Picture, LayoutCategory.Image, LayoutCategory.Figure };

        var pictureBlocks = blocks.Where(b =>
            picCategories.Any(c => b.Category == c) &&
            b.Bbox is { Count: 4 }).ToList();

        if (pictureBlocks.Count == 0)
            return new Dictionary<string, string>();

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
                    string bboxKey = $"{(int)block.Bbox[0]}_{(int)block.Bbox[1]}_{(int)block.Bbox[2]}_{(int)block.Bbox[3]}.jpg";
                    images[bboxKey] = croppedBase64;

                    if (!string.IsNullOrEmpty(block.Text))
                    {
                        var match = Regex.Match(block.Text, @"!\[([^\]]*)\]\([^)]+\)");
                        if (match.Success)
                        {
                            string altText = match.Groups[1].Value;
                            block.Text = $"![{altText}]({bboxKey})";
                        }
                        else
                        {
                            block.Text = $"![{contentType ?? "image/jpeg"}]({bboxKey})";
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Silent fail for crop
            }
        }));

        await Task.WhenAll(tasks);
        return images.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public virtual async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_baseUrl) || string.IsNullOrEmpty(_apiKey))
            return false;

        try
        {
            _logger.LogDebug("[PING] Pinging OpenAI API");
            _logger.LogDebug($"[PING] Base URL: {_baseUrl}");
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl.TrimEnd('/') + "/models");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public abstract ChatRequestMessage CreateMessage(string prompt, string base64Image);

    /// <summary>
    /// Detects if the content contains a large sequence of repeated text, indicating a model loop.
    /// </summary>
    protected virtual bool IsRepetitive(string content, int windowSize = 500)
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
}
