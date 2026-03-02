using System.Text;
using Azure;
using Azure.AI.Inference;
using OCREngine.Applications.Interfaces;
using OCREngine.Factories;
using OCREngine.Helpers;
using OCREngine.Models;
using OCREngine.Models.Enum;
using OCREngine.Utils;

namespace OCREngine.Infrastructure.Services;

public class DotsOcrService : BaseOcrEngine
{

    public DotsOcrService(OpenAiClientFactory openAiClientFactory, ILogger<DotsOcrService> logger)
    : base(LlmSupport.Dots, openAiClientFactory, logger)
    {
    }

    protected override async Task<OcrResponse> OcrImageCoreAsync(
        OcrImageRequest request,
        CancellationToken cancellationToken)
    {
        if (request?.Image == null || string.IsNullOrEmpty(request.Image.Base64))
            throw new ArgumentNullException(nameof(request));

        string prompt = LlmUtil.GetDefaultPrompt(LlmSupport.Dots);

        var completionOptions = new ChatCompletionsOptions
        {
            Messages = { CreateMessage(prompt, request.Image.Base64) },
            Model = _modelName,
            MaxTokens = request.MaxTokens,
            Temperature = 0.1f,
            NucleusSamplingFactor = 0.9f,
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

    public override string ConvertPageToMarkdown(List<LayoutBlock> page, bool includeHeaderFooter = false)
        => DotsOcrHelper.ToMarkdown(page, !includeHeaderFooter);

    public override ChatRequestMessage CreateMessage(string prompt, string base64Image)
    {
        string formattedPrompt = $"<|img|><|imgpad|><|endofimg|>{prompt}";
        return new ChatRequestUserMessage(new List<ChatMessageContentItem>
        {
            new ChatMessageImageContentItem(BinaryData.FromBytes(Convert.FromBase64String(base64Image)), "image/jpeg"),
            new ChatMessageTextContentItem(formattedPrompt)
        });
    }

    protected override string CleanRawResponse(string text)
    {
        return DotsOcrHelper.CleanRawResponse(text);
    }
}