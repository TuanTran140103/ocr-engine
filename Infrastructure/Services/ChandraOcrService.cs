using Azure;
using Azure.AI.Inference;
using OCREngine.Applications.Interfaces;
using OCREngine.Factories;
using OCREngine.Models;
using OCREngine.Models.Enum;
using OCREngine.Helpers;
using System.Text;
using OCREngine.Utils;
using System.Text.RegularExpressions;

namespace OCREngine.Infrastructure.Services;

public class ChandraOcrService : BaseOcrEngine
{

    public ChandraOcrService(OpenAiClientFactory openAiClientFactory, ILogger<ChandraOcrService> logger)
    : base(LlmSupport.Chandra, openAiClientFactory, logger)
    {
    }

    public override ChatRequestMessage CreateMessage(string prompt, string base64Image)
    {
        var imageContent = new ChatMessageImageContentItem(
            BinaryData.FromBytes(Convert.FromBase64String(base64Image)),
            "image/jpeg"
        );
        var textContent = new ChatMessageTextContentItem(prompt);
        var userMessage = new ChatRequestUserMessage(new List<ChatMessageContentItem>
        {
            imageContent,
            textContent
        });
        return userMessage;
    }

    public override string ConvertPageToMarkdown(List<LayoutBlock> page, bool includeHeaderFooter = false)
        => ChandraOcrHelper.ToMarkdown(page, !includeHeaderFooter);

    protected override string CleanRawResponse(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        // Xóa ```html hoặc ``` ở đầu và cuối
        string cleaned = Regex.Replace(raw, @"^```(html)?\s*", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*```$", "");
        return cleaned.Trim();
    }

    protected override async Task<OcrResponse> OcrImageCoreAsync(
        OcrImageRequest request,
        CancellationToken cancellationToken)
    {
        string prompt = LlmUtil.GetDefaultPrompt(LlmSupport.Chandra);

        var completionOptions = new ChatCompletionsOptions
        {
            Messages = { CreateMessage(prompt, request.Image.Base64) },
            Model = _modelName,
            MaxTokens = request.MaxTokens,
            Temperature = 0.1f,
            NucleusSamplingFactor = 0.1f,
        };

        if (request.FrequencyPenalty.HasValue)
            completionOptions.FrequencyPenalty = request.FrequencyPenalty.Value;
        if (request.PresencePenalty.HasValue)
            completionOptions.PresencePenalty = request.PresencePenalty.Value;

        var chatCompletionResponse = await _chatClient.CompleteAsync(completionOptions, cancellationToken);
        ChatCompletions chatCompletion = chatCompletionResponse.Value;

        return new OcrResponse
        {
            Content = chatCompletion.Content ?? string.Empty,
            FinishReason = chatCompletion.FinishReason?.ToString().ToLowerInvariant() ?? "",
            TokenCount = (int)(chatCompletion.Usage?.CompletionTokens ?? 0)
        };
    }

    protected override async Task<List<LayoutBlock>> ParseResponseToLayoutBlocksAsync(string content, ProcessedImage image)
    {
        return await ChandraOcrHelper.ParseLayoutAsync(content, image.Width, image.Height);
    }
}