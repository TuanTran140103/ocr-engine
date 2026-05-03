using Azure.AI.Inference;
using OCREngine.Applications.Interfaces;
using OCREngine.Factories;
using OCREngine.Models;
using OCREngine.Models.Enum;
using OCREngine.Prompts;
using OCREngine.Helpers;

namespace OCREngine.Infrastructure.Services;

public class ChandraOcrService : BaseOcrEngine
{
    private readonly HttpClient _httpClient;

    public ChandraOcrService(
        OpenAiClientFactory openAiClientFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<ChandraOcrService> logger,
        IWebHostEnvironment env)
    : base(LlmSupport.ChandraOcr, openAiClientFactory, logger, env)
    {
        string apiKey = openAiClientFactory.GetApiKey(LlmSupport.ChandraOcr);

        _httpClient = httpClientFactory.CreateClient("ChandraOcr");
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    // ── Message creation ──

    public override ChatRequestMessage CreateMessage(string prompt, string base64Image)
    {
        var textPart = new ChatMessageTextContentItem(prompt);
        return new ChatRequestUserMessage(new List<ChatMessageContentItem> { textPart });
    }

    protected override string CleanRawResponse(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Trim();
    }

    protected override async Task<OcrResponse> OcrImageCoreAsync(
        OcrImageRequest request,
        CancellationToken cancellationToken)
    {
        bool isRetry = request.FrequencyPenalty.HasValue;

        string promptRetry = """
        Convert document to markdown. Do not caption images, diagram.
        """;

        // 2 page đầu dùng PromptOcr, còn lại dùng PromptLayoutAndOcr
        // Khi retry thì luôn dùng PromptLayoutAndOcr
        string fullPrompt = (request.PageIndex < 2 && !isRetry)
            ? ChandraOcrPrompt.PromptOcr
            : promptRetry;

        // Build message content: image first, then text (matching Python implementation)
        var contentItems = new List<ChatMessageContentItem>
        {
            // 1. Image first
            new ChatMessageImageContentItem(
                BinaryData.FromBytes(Convert.FromBase64String(request.Image.Base64)),
                request.Image.ContentType),
            // 2. Text after
            new ChatMessageTextContentItem(fullPrompt)
        };

        var options = new ChatCompletionsOptions
        {
            Model = _modelName,
            Messages =
            {
                new ChatRequestUserMessage(contentItems)
            },
            Temperature = isRetry ? 0.2f : 0.0f,
            MaxTokens = request.MaxTokens == 4096 ? 5000 : 5000
        };

        // Set top_p = 0.1
        options.AdditionalProperties["top_p"] = BinaryData.FromObjectAsJson(0.1f);

        var response = await _chatClient!.CompleteAsync(options, cancellationToken);
        var chatCompletion = response.Value;

        return new OcrResponse
        {
            Content = chatCompletion.Content ?? "",
            FinishReason = chatCompletion.FinishReason?.ToString()?.ToLowerInvariant() ?? "stop",
            TokenCount = (int)(chatCompletion.Usage?.CompletionTokens ?? 0)
        };
    }

    /// <summary>
    /// response of chandra return html, so parsion is mapping content --> layout.Text
    /// convert to markdown, image will assign key_image to attribute src
    /// </summary>
    /// <param name="content"></param>
    /// <param name="request"></param>
    /// <returns></returns>
    protected override async Task<List<LayoutBlock>> ParseResponseToLayoutBlocksAsync(
        string content, OcrImageRequest request)
    {
        var image = request.Image;
        var parse = ChandraOcrHelper.ParseLayoutBlocks(content, image.Width, image.Height);

        // Mode "Free Ocr." hoặc khi không có tag layout -> trả về 0 block.
        // Ta sẽ wrap toàn bộ content vào 1 block duy nhất để không bị rỗng kết quả.
        if (parse.Count == 0 && !string.IsNullOrWhiteSpace(content))
        {
            parse.Add(new LayoutBlock { Category = LayoutCategory.Text, Text = content });
        }

        // Tiền xử lý Diagram/Figure/Image blocks: loại bỏ caption, chỉ giữ lại <img>
        var diagramCategories = new[] { LayoutCategory.Figure, LayoutCategory.Image, LayoutCategory.Picture };
        foreach (var block in parse.Where(b => diagramCategories.Any(c => b.Category == c) && !string.IsNullOrEmpty(b.Text)))
        {
            block.Text = await ChandraOcrHelper.CleanDiagramBlockTextAsync(block.Text!);
        }

        return parse;
    }

    /// <summary>
    /// Extract images từ Table blocks.
    /// Loại bỏ data-bbox khỏi các thẻ table, td, tr, th... và giữ nguyên data-bbox cho thẻ img.
    /// Thêm src attribute trỏ vào bbox key cho các thẻ img.
    /// </summary>
    public override async Task<Dictionary<string, string>> ExtractImagesFromTableBlocksAsync(
        List<LayoutBlock> page, string base64Image)
    {
        var allImages = new Dictionary<string, string>();

        // Tìm tất cả Table blocks
        var tableBlocks = page.Where(b => b.Category == LayoutCategory.Table && !string.IsNullOrEmpty(b.Text)).ToList();

        // Lấy dimensions từ ảnh gốc
        int imageWidth = 0;
        int imageHeight = 0;

        if (!string.IsNullOrEmpty(base64Image))
        {
            try
            {
                var dims = ImageHelper.GetImageDimensions(Convert.FromBase64String(base64Image));
                imageWidth = dims.Width;
                imageHeight = dims.Height;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get image dimensions for table extraction");
            }
        }

        foreach (var tableBlock in tableBlocks)
        {
            var tableImages = await ChandraOcrHelper.ExtractImagesFromTableBlocks(tableBlock, base64Image, imageWidth, imageHeight);
            foreach (var kvp in tableImages)
            {
                allImages[kvp.Key] = kvp.Value;
            }
        }

        return allImages;
    }

    /// <summary>
    /// Override để crop ảnh từ bbox và gán src attribute trỏ vào bbox key.
    /// Kết quả trả về là HTML nên không cần convert sang markdown như các model khác.
    /// Xử lý tuần tự từng block.
    /// </summary>
    public override async Task<Dictionary<string, string>> TransformBboxImageToBase64Async(
        string? base64Image, List<LayoutBlock> blocks, string? contentType = null)
    {
        var images = new Dictionary<string, string>();

        if (blocks == null || string.IsNullOrEmpty(base64Image))
            return images;

        var picCategories = new[] { LayoutCategory.Picture, LayoutCategory.Image, LayoutCategory.Figure };

        var pictureBlocks = blocks.Where(b =>
            picCategories.Any(c => b.Category == c) &&
            b.Bbox is { Count: 4 }).ToList();

        if (pictureBlocks.Count == 0)
            return images;

        foreach (var block in pictureBlocks)
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

                    // Update Text (HTML) của block: thêm src attribute vào thẻ img
                    if (!string.IsNullOrEmpty(block.Text))
                    {
                        block.Text = await ChandraOcrHelper.UpdateImgSrcAttributeAsync(block.Text, bboxKey);
                    }
                }
            }
            catch (Exception)
            {
                // Silent fail for crop
            }
        }

        await Task.CompletedTask;
        return images;
    }

    /// <summary>
    /// Converts layout blocks to Markdown với hỗ trợ HTML từ Chandra model.
    /// Xử lý:
    /// 1. Crop ảnh từ Picture/Image/Figure blocks
    /// 2. Extract ảnh từ Table blocks (với xử lý data-bbox đặc thù)
    /// 3. Convert HTML → Markdown (giữ nguyên Table HTML và Image tags)
    /// </summary>
    public override async Task<PageOcrResult> ConvertPageToMarkdownAsync(
        List<LayoutBlock> page, string? base64Image, int pageIndex, bool includeHeaderFooter = false, string? contentType = null)
    {
        var images = new Dictionary<string, string>();

        // 1. Crop ảnh từ Picture/Image/Figure blocks
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

            // 2. Extract ảnh từ Table blocks
            var tableImages = await ExtractImagesFromTableBlocksAsync(page, base64Image);
            foreach (var kvp in tableImages)
            {
                images[kvp.Key] = kvp.Value;
            }
        }

        // 3. Convert LayoutBlocks sang Markdown (dùng helper, giữ nguyên Table HTML và Image tags)
        var markdown = ChandraOcrHelper.ConvertBlocksToMarkdown(page);

        return new PageOcrResult
        {
            PageIndex = pageIndex,
            Markdown = markdown,
            Images = images
        };
    }
}
