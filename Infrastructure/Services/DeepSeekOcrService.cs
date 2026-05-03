using Azure.AI.Inference;
using OCREngine.Applications.Interfaces;
using OCREngine.Factories;
using OCREngine.Models;
using OCREngine.Models.Enum;
using OCREngine.Helpers;
using OCREngine.Prompts;

namespace OCREngine.Infrastructure.Services;

public class DeepSeekOcrService : BaseOcrEngine
{
    private readonly HttpClient _httpClient;

    public DeepSeekOcrService(
        OpenAiClientFactory openAiClientFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<DeepSeekOcrService> logger,
        IWebHostEnvironment env)
    : base(LlmSupport.DeepSeekOcr, openAiClientFactory, logger, env)
    {
        string apiKey = openAiClientFactory.GetApiKey(LlmSupport.DeepSeekOcr);

        _httpClient = httpClientFactory.CreateClient("DeepSeekOcr");
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    // ── Message creation ──

    public override ChatRequestMessage CreateMessage(string prompt, string base64Image)
    {
        // This is still needed for BaseOcrEngine signature, but we'll use manual JSON in OcrImageCoreAsync
        var textPart = new ChatMessageTextContentItem($"<image>\n<|grounding|> {prompt}");
        return new ChatRequestUserMessage(new List<ChatMessageContentItem> { textPart });
    }

    protected override string CleanRawResponse(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = text.Replace("<｜end▁of▁sentence｜>", "");
        return text.Trim();
    }

    protected override async Task<OcrResponse> OcrImageCoreAsync(
        OcrImageRequest request,
        CancellationToken cancellationToken)
    {

        bool isRetry = request.FrequencyPenalty.HasValue;

        string fullPrompt = DeepSeekOcrPrompt.PromptLayoutAndOcr;

        if (request.PageIndex < 2)
        {
            fullPrompt = DeepSeekOcrPrompt.PromptOcr;
        }

        var options = new ChatCompletionsOptions
        {
            Model = _modelName,
            Messages =
            {
                new ChatRequestUserMessage(
                    // 1. Image trước (Dùng BinaryData để tránh lỗi UriTooLong)
                    new ChatMessageImageContentItem(BinaryData.FromBytes(Convert.FromBase64String(request.Image.Base64)), "image/png"),
                    // 2. Text sau
                    new ChatMessageTextContentItem(fullPrompt)
                )
            },
            Temperature = isRetry ? 0.2f : 0.0f,
            MaxTokens = request.MaxTokens
        };

        if (request.FrequencyPenalty.HasValue)
            options.FrequencyPenalty = request.FrequencyPenalty.Value;
        if (request.PresencePenalty.HasValue)
            options.PresencePenalty = request.PresencePenalty.Value;

        // Bổ sung các tham số extra đặc thù của DeepSeek-OCR2 (vLLM)
        options.AdditionalProperties["skip_special_tokens"] = BinaryData.FromObjectAsJson(false);
        options.AdditionalProperties["include_stop_str_in_output"] = BinaryData.FromObjectAsJson(true);

        if (isRetry)
        {
            options.AdditionalProperties["repetition_penalty"] = BinaryData.FromObjectAsJson(1.05f);
        }

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
        var image = request.Image;
        var parse = DeepSeekOcrHelper.ParseLayoutBlocks(content);

        // Mode "Free Ocr." hoặc khi không có tag layout -> trả về 0 block.
        // Ta sẽ wrap toàn bộ content vào 1 block duy nhất để không bị rỗng kết quả.
        if (parse.Count == 0 && !string.IsNullOrWhiteSpace(content))
        {
            parse.Add(new LayoutBlock { Category = LayoutCategory.Text, Text = content });
        }

        foreach (var block in parse)
        {
            if (block.Category == LayoutCategory.Title)
            {
                bool isMissingHeaderLevel = !block.Text!.TrimStart().StartsWith("#");
                if (isMissingHeaderLevel)
                    block.Text = "### " + block.Text!.TrimStart();
            }
            if (block.Bbox != null)
            {
                block.Bbox = DeepSeekOcrHelper.ScaleToReal(block.Bbox, image.Width, image.Height);

                if (string.IsNullOrWhiteSpace(block.Text) && block.Bbox != null && block.Bbox.Count >= 4 &&
                    (block.Category == LayoutCategory.Image || block.Category == LayoutCategory.Figure || block.Category == LayoutCategory.Picture))
                {
                    string bboxKey = $"{(int)block.Bbox[0]}_{(int)block.Bbox[1]}_{(int)block.Bbox[2]}_{(int)block.Bbox[3]}.jpg";
                    block.Text = $"![{request.Image.ContentType}]({bboxKey})";
                }
            }
        }
        return Task.FromResult(parse);
    }
}
