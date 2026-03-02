using OCREngine.Models.Enum;

namespace OCREngine.Models;

public class OcrResponse
{
    public string Content { get; set; } = string.Empty;
    public string FinishReason { get; set; } = string.Empty;
    public int TokenCount { get; set; }
}


public class OcrEvent
{
    public required string TaskId { get; set; } = string.Empty;
    public required EventStatus Status { get; set; }
    public required string Message { get; set; } = string.Empty;
    public required string Filename { get; set; } = string.Empty;
    public string Timestamp { get; init; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public required EventType EventType { get; set; }
    public string? DataJson { get; set; }
    public double? ProcessingTime { get; set; }
}

public class OcrRequest
{
    public required string TaskId { get; set; } = string.Empty;
    public required string Filename { get; set; } = string.Empty;
    public required string Prompt { get; set; } = string.Empty;
    public int MyProperty { get; set; }
}

public class ProcessedImage
{
    public string Base64 { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/jpeg"; // Default to JPEG
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// Input object truyền vào Engine layer: chứa image đã xử lý và OpenAI API params.
/// Rotation đã được áp dụng vào ảnh trước khi tạo request này — engine không cần biết góc xoay.
/// </summary>
public class OcrImageRequest
{
    public required ProcessedImage Image { get; set; }

    // --- OpenAI API params (điều chỉnh qua mỗi lần retry) ---
    public int MaxTokens { get; set; } = 1024 * 4;
    public float? FrequencyPenalty { get; set; } = null;
    public float? PresencePenalty { get; set; } = null;
    public int PageIndex { get; set; } = -1;

    /// <summary>
    /// Optional rotation applied to the image (for auditing/logging)
    /// </summary>
    public float RotationDegrees { get; set; } = 0;
}