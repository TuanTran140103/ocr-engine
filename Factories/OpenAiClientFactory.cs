using Azure;
using Azure.AI.Inference;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Options;
using OCREngine.Models.Enum;
using OCREngine.Options;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Reflection;

namespace OCREngine.Factories;

public class OpenAiClientFactory
{
    private readonly LlmModelsOption _modelOptions;
    private static readonly ConcurrentDictionary<LlmSupport, PropertyInfo> ModelPropertyCache = new();
    private static readonly ConcurrentDictionary<LlmSupport, ChatCompletionsClient> ClientCache = new();

    public OpenAiClientFactory(IOptions<LlmModelsOption> modelOptions)
    {
        _modelOptions = modelOptions.Value;
    }

    public ChatCompletionsClient CreateChatClient(LlmSupport llmSupport)
    {
        return ClientCache.GetOrAdd(llmSupport, key =>
        {
            var modelOption = GetModelOption(key);
            if (modelOption == null)
                throw new ArgumentException($"Configuration for model {key} not found in appsettings.json");

            if (string.IsNullOrEmpty(modelOption.BaseUrl))
                throw new ArgumentException($"BaseUrl for model {key} is not configured.");

            var clientOptions = new AzureAIInferenceClientOptions
            {
                Transport = new HttpClientTransport(new HttpClient { Timeout = TimeSpan.FromSeconds(600) }),
                Retry =
                {
                    Mode = RetryMode.Fixed,
                    MaxRetries = 0,
                    NetworkTimeout = TimeSpan.FromSeconds(300) // Tăng timeout để tránh lỗi "exceeded the configured timeout of 0:01:40"
                }
            };
            // Tắt retry của SDK vì chúng ta đã có logic retry thủ công ở tầng Engine rồi

            // vLLM/Modal yêu cầu Authorization: Bearer thay vì api-key header mặc định của Azure
            clientOptions.AddPolicy(new BearerTokenPolicy(modelOption.ApiKey ?? ""), Azure.Core.HttpPipelinePosition.PerCall);

            return new ChatCompletionsClient(
                endpoint: new Uri(modelOption.BaseUrl),
                credential: new AzureKeyCredential(modelOption.ApiKey ?? ""),
                options: clientOptions
            );
        });
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

    private ModelOption? GetModelOption(LlmSupport llmSupport)
    {
        var propertyInfo = ModelPropertyCache.GetOrAdd(llmSupport, key =>
        {
            // Map enum name to property name: DeepSeekOcr -> DeepSeek, ChandraOcr -> Chandra
            var enumName = key.ToString();
            var propertyName = enumName.EndsWith("Ocr") 
                ? enumName[..^3] // Remove "Ocr" suffix
                : enumName;

            return typeof(LlmModelsOption).GetProperty(propertyName) 
                ?? throw new ArgumentException($"No property '{propertyName}' found in LlmModelsOption for enum '{key}'");
        });

        return propertyInfo.GetValue(_modelOptions) as ModelOption;
    }

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
