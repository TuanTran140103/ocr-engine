namespace OCREngine.Options;

public class ModelOption
{
    public required string ModelName { get; set; }
    public string? ApiKey { get; set; }
    public required string BaseUrl { get; set; }
    public int Concurrency { get; set; } = 1;
}

public class LlmModelsOption
{
    public ModelOption? Dots { get; set; }
    public ModelOption? Chandra { get; set; }
    public ModelOption? DeepSeek { get; set; }
}
