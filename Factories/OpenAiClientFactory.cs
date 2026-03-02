using Azure;
using Azure.AI.Inference;
using Microsoft.Extensions.Options;
using OCREngine.Models.Enum;
using OCREngine.Options;

namespace OCREngine.Factories;

public class OpenAiClientFactory
{
    private readonly LlmModelsOption _modelOptions;

    public OpenAiClientFactory(IOptions<LlmModelsOption> modelOptions)
    {
        _modelOptions = modelOptions.Value;
    }

    public ChatCompletionsClient CreateChatClient(LlmSupport llmSupport)
    {
        ModelOption? modelOption = llmSupport switch
        {
            LlmSupport.Dots => _modelOptions.Dots,
            LlmSupport.Chandra => _modelOptions.Chandra,
            LlmSupport.DeepSeekOcr => _modelOptions.DeepSeek,
            _ => throw new ArgumentException($"Model support for {llmSupport} is not implemented in factory.")
        };

        if (modelOption == null)
            throw new ArgumentException($"Configuration for model {llmSupport} not found in appsettings.json");

        if (string.IsNullOrEmpty(modelOption.BaseUrl))
            throw new ArgumentException($"BaseUrl for model {llmSupport} is not configured.");

        var clientOptions = new AzureAIInferenceClientOptions();
        // vLLM/Modal yêu cầu Authorization: Bearer thay vì api-key header mặc định của Azure
        clientOptions.AddPolicy(new BearerTokenPolicy(modelOption.ApiKey ?? ""), Azure.Core.HttpPipelinePosition.PerCall);

        return new ChatCompletionsClient(
            endpoint: new Uri(modelOption.BaseUrl),
            credential: new AzureKeyCredential(modelOption.ApiKey ?? ""),
            options: clientOptions
        );
    }

    public string GetModelName(LlmSupport llmSupport)
    {
        ModelOption? modelOption = GetModelOption(llmSupport);
        return modelOption?.ModelName ?? string.Empty;
    }

    public string GetBaseUrl(LlmSupport llmSupport)
    {
        ModelOption? modelOption = GetModelOption(llmSupport);
        return modelOption?.BaseUrl ?? string.Empty;
    }

    public string GetApiKey(LlmSupport llmSupport)
    {
        ModelOption? modelOption = GetModelOption(llmSupport);
        return modelOption?.ApiKey ?? string.Empty;
    }

    private ModelOption? GetModelOption(LlmSupport llmSupport) => llmSupport switch
    {
        LlmSupport.Dots => _modelOptions.Dots,
        LlmSupport.Chandra => _modelOptions.Chandra,
        LlmSupport.DeepSeekOcr => _modelOptions.DeepSeek,
        _ => null
    };

    private class BearerTokenPolicy : Azure.Core.Pipeline.HttpPipelinePolicy
    {
        private readonly string _token;
        public BearerTokenPolicy(string token) => _token = token;

        public override void Process(Azure.Core.HttpMessage message, ReadOnlyMemory<Azure.Core.Pipeline.HttpPipelinePolicy> pipeline)
        {
            if (!string.IsNullOrEmpty(_token))
                message.Request.Headers.SetValue("Authorization", $"Bearer {_token}");
            ProcessNext(message, pipeline);
        }

        public override async ValueTask ProcessAsync(Azure.Core.HttpMessage message, ReadOnlyMemory<Azure.Core.Pipeline.HttpPipelinePolicy> pipeline)
        {
            if (!string.IsNullOrEmpty(_token))
                message.Request.Headers.SetValue("Authorization", $"Bearer {_token}");
            await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
        }
    }
}
