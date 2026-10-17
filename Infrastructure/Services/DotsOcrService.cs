using Azure.AI.Inference;
using OCREngine.Applications.Interfaces;
using OCREngine.Factories;
using OCREngine.Models;
using OCREngine.Models.Enum;
using OCREngine.Prompts;
using OCREngine.Helpers;
using System.Collections.Concurrent;

namespace OCREngine.Infrastructure.Services;

public class DotsOcrService : BaseOcrEngine
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<int, (int origW, int origH, int procW, int procH)> _pageDimensions = new();

    public DotsOcrService(
        OpenAiClientFactory openAiClientFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<DotsOcrService> logger,
        IWebHostEnvironment env)
    : base(LlmSupport.DotsOcr, openAiClientFactory, logger, env)
    {
        string apiKey = openAiClientFactory.GetApiKey(LlmSupport.DotsOcr);

        _httpClient = httpClientFactory.CreateClient("DotsOcr");
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    public override ChatRequestMessage CreateMessage(string prompt, string base64Image)
    {
        var textPart = new ChatMessageTextContentItem(prompt);
        return new ChatRequestUserMessage(new List<ChatMessageContentItem> { textPart });
    }

    protected override string CleanRawResponse(string text)
    {
        return DotsOcrHelper.CleanRawResponse(text);
    }

    protected override async Task<OcrResponse> OcrImageCoreAsync(
        OcrImageRequest request,
        CancellationToken cancellationToken)
    {
        bool isRetry = request.FrequencyPenalty.HasValue;

        if (isRetry)
        {
            return await CallOcrApiAsync(request, DotsOcrPrompt.PromptOcr, temperature: 0.4f,
                isRetry: true, cancellationToken);
        }

        // First attempt: prompt_layout_all → JSON layout
        var response = await CallOcrApiAsync(request, DotsOcrPrompt.PromptLayoutAll, temperature: 0.0f,
            isRetry: false, cancellationToken);

        string cleaned = DotsOcrHelper.CleanRawResponse(response.Content);

        // If response is NOT a JSON array → immediately retry with PromptOcr
        if (!cleaned.TrimStart().StartsWith('['))
        {
            _logger.LogWarning(
                "[DOTSOCR] Page {Page} — model did not return JSON array. Retrying with PromptOcr (temp=0.4).",
                request.PageIndex + 1);

            return await CallOcrApiAsync(request, DotsOcrPrompt.PromptOcr, temperature: 0.4f,
                isRetry: true, cancellationToken);
        }

        // Validate JSON: try parse → if fail, retry with PromptOcr
        try
        {
            var rawBlocks = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(cleaned);
            if (rawBlocks == null)
            {
                return await CallOcrApiAsync(request, DotsOcrPrompt.PromptOcr, temperature: 0.4f,
                    isRetry: true, cancellationToken);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            _logger.LogWarning(
                "[DOTSOCR] Page {Page} — model returned malformed JSON. Retrying with PromptOcr (temp=0.4).",
                request.PageIndex + 1);

            return await CallOcrApiAsync(request, DotsOcrPrompt.PromptOcr, temperature: 0.4f,
                isRetry: true, cancellationToken);
        }

        return response;
    }

    private async Task<OcrResponse> CallOcrApiAsync(
        OcrImageRequest request,
        string prompt,
        float temperature,
        bool isRetry,
        CancellationToken cancellationToken)
    {
        byte[] originalBytes = Convert.FromBase64String(request.Image.Base64);
        int origW = request.Image.Width;
        int origH = request.Image.Height;

        var processed = DotsOcrHelper.NormalizeImageToBase64(originalBytes);

        _pageDimensions[request.PageIndex] = (origW, origH, processed.Width, processed.Height);

        var contentItems = new List<ChatMessageContentItem>
        {
            new ChatMessageImageContentItem(
                BinaryData.FromBytes(Convert.FromBase64String(processed.Base64)),
                processed.ContentType),
            new ChatMessageTextContentItem(prompt)
        };

        var options = new ChatCompletionsOptions
        {
            Model = _modelName,
            Messages = { new ChatRequestUserMessage(contentItems) },
            Temperature = temperature,
            MaxTokens = 5000,
        };

        options.AdditionalProperties["top_p"] = BinaryData.FromObjectAsJson(isRetry ? 0.4f : 0.1f);

        var response = await _chatClient!.CompleteAsync(options, cancellationToken);
        var chatCompletion = response.Value;

        return new OcrResponse
        {
            Content = chatCompletion.Content ?? "",
            FinishReason = chatCompletion.FinishReason?.ToString()?.ToLowerInvariant() ?? "stop",
            TokenCount = (int)(chatCompletion.Usage?.CompletionTokens ?? 0)
        };
    }

    protected override Task<List<LayoutBlock>> ParseResponseToLayoutBlocksAsync(
        string content, OcrImageRequest request)
    {
        string trimmed = content.TrimStart();

        // Markdown từ PromptOcr → wrap trong single Text block
        if (!trimmed.StartsWith('['))
        {
            var blocks = new List<LayoutBlock>();
            if (!string.IsNullOrWhiteSpace(content))
            {
                blocks.Add(new LayoutBlock { Category = LayoutCategory.Text, Text = content });
            }
            return Task.FromResult(blocks);
        }

        // JSON layout → parse với bbox scaling
        if (_pageDimensions.TryRemove(request.PageIndex, out var dims))
        {
            var blocks = DotsOcrHelper.ParseLayoutBlocks(
                content, dims.origW, dims.origH, dims.procW, dims.procH);
            return Task.FromResult(blocks);
        }

        // Fallback: parse without scaling
        try
        {
            var blocks = System.Text.Json.JsonSerializer.Deserialize<List<LayoutBlock>>(content) ?? [];
            return Task.FromResult(blocks);
        }
        catch
        {
            return Task.FromResult(new List<LayoutBlock>());
        }
    }

    public override async Task<PageOcrResult> ConvertPageToMarkdownAsync(
        List<LayoutBlock> page, string? base64Image, int pageIndex,
        bool includeHeaderFooter = false, string? contentType = null)
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
        }

        var markdown = DotsOcrHelper.ConvertBlocksToMarkdown(page);

        return new PageOcrResult
        {
            PageIndex = pageIndex,
            Markdown = markdown,
            Images = images
        };
    }
}
