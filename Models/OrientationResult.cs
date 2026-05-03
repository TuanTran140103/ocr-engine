using System.Text.Json.Serialization;

namespace OCREngine.Models;

public class OrientationResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("orientation")]
    public string Orientation { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}

public class BatchOrientationResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("predictions")]
    public List<OrientationResult> Predictions { get; set; } = new();

    [JsonPropertyName("processed")]
    public int Processed { get; set; }

    [JsonPropertyName("total_uploaded")]
    public int TotalUploaded { get; set; }
}
