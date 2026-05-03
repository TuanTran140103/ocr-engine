

using System.Text.Json.Serialization;
using OCREngine.Models.Enum;

namespace OCREngine.Models;

public class GenerationResult
{
    public string Raw { get; set; } = string.Empty;
    public int TokenCount { get; set; }
    public bool Error { get; set; }
    public GenerationResult() { }
    public GenerationResult(string raw, int tokenCount, bool error = false)
    {
        Raw = raw;
        TokenCount = tokenCount;
        Error = error;
    }
}
// Dữ liệu đầu vào cho 1 Task OCR
public class BatchInputItem
{
    public byte[] ImageBytes { get; set; } = Array.Empty<byte>();
    public string? Prompt { get; set; }
    public string? PromptType { get; set; }
    public BatchInputItem() { }
    public BatchInputItem(byte[] imageBytes, string? prompt = null, string? promptType = null)
    {
        ImageBytes = imageBytes;
        Prompt = prompt;
        PromptType = promptType;
    }
}
// Một khối layout 
public class LayoutBlock
{
    /// <summary>
    /// Tọa độ vùng chứa: [x1, y1, x2, y2]
    /// </summary>
    [JsonPropertyName("bbox")]
    public List<float>? Bbox { get; set; }

    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LayoutCategory? Category { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; } = string.Empty;

    [JsonIgnore]
    public int X1 => Bbox?.Count > 0 ? (int)Bbox[0] : 0;
    [JsonIgnore]
    public int Y1 => Bbox?.Count > 1 ? (int)Bbox[1] : 0;
    [JsonIgnore]
    public int X2 => Bbox?.Count > 2 ? (int)Bbox[2] : 0;
    [JsonIgnore]
    public int Y2 => Bbox?.Count > 3 ? (int)Bbox[3] : 0;
}
// Kết quả cuối cùng sau khi Parse
public class BatchOutputItem
{
    public string Markdown { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
    public Dictionary<string, object> Chunks { get; set; } = new();
    public string Raw { get; set; } = string.Empty;
    public List<int> PageBox { get; set; } = new();
    public int TokenCount { get; set; }
    public Dictionary<string, string> Images { get; set; } = new();
    public bool Error { get; set; }
}