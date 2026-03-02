using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using OCREngine.Applications.Interfaces;
using OCREngine.Models;
using OCREngine.Options;
using System.Text.Json;

namespace OCREngine.Infrastructure.ExternalService;

public class DocOriService : IDocOriService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DocOriService> _logger;
    private readonly DocOriOption _options;

    // Global semaphore to limit concurrent requests to the rotation server
    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    public DocOriService(
        IHttpClientFactory httpClientFactory,
        IOptions<ExternalServiceOption> options,
        ILogger<DocOriService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("DocOriClient");
        _logger = logger;
        _options = options.Value.DocOri;
    }

    public async Task<OrientationResult> PredictAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Image file not found", filePath);
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            using var form = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(filePath);
            using var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg"); // Adjust if necessary

            form.Add(streamContent, "file", Path.GetFileName(filePath));

            var response = await _httpClient.PostAsync($"{_options.BaseUrl}/predict", form, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<OrientationResult>(content)
                   ?? throw new Exception("Failed to deserialize orientation result");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<OrientationResult> PredictAsync(byte[] imageBytes, string fileName, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            using var form = new MultipartFormDataContent();
            using var byteContent = new ByteArrayContent(imageBytes);
            byteContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

            form.Add(byteContent, "file", fileName);

            var response = await _httpClient.PostAsync($"{_options.BaseUrl}/predict", form, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<OrientationResult>(content)
                   ?? throw new Exception("Failed to deserialize orientation result");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<BatchOrientationResult> PredictBatchAsync(List<string> filePaths, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            using var form = new MultipartFormDataContent();

            var streams = new List<FileStream>();
            try
            {
                foreach (var path in filePaths)
                {
                    if (File.Exists(path))
                    {
                        var fileStream = File.OpenRead(path);
                        streams.Add(fileStream);
                        var streamContent = new StreamContent(fileStream);
                        streamContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                        form.Add(streamContent, "files", Path.GetFileName(path));
                    }
                }

                var response = await _httpClient.PostAsync($"{_options.BaseUrl}/predict-batch", form, cancellationToken);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<BatchOrientationResult>(content)
                       ?? throw new Exception("Failed to deserialize batch orientation result");
            }
            finally
            {
                foreach (var stream in streams)
                {
                    await stream.DisposeAsync();
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<BatchOrientationResult> PredictBatchAsync(List<(byte[] Bytes, string FileName)> images, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            using var form = new MultipartFormDataContent();

            foreach (var (bytes, fileName) in images)
            {
                var byteContent = new ByteArrayContent(bytes);
                byteContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(byteContent, "files", fileName);
            }

            var response = await _httpClient.PostAsync($"{_options.BaseUrl}/predict-batch", form, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<BatchOrientationResult>(content)
                   ?? throw new Exception("Failed to deserialize batch orientation result");
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
