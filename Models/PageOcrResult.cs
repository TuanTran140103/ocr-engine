namespace OCREngine.Models;

/// <summary>
/// Kết quả OCR của một trang, bao gồm markdown và các ảnh đã crop (dưới dạng base64).
/// </summary>
public class PageOcrResult
{
    /// <summary>
    /// Chỉ số trang (0-based).
    /// </summary>
    public int PageIndex { get; set; }

    /// <summary>
    /// Nội dung markdown của trang.
    /// </summary>
    public string Markdown { get; set; } = string.Empty;

    /// <summary>
    /// Dictionary chứa các ảnh đã crop từ trang.
    /// Key: bbox dưới dạng "x1_y1_x2_y2.extension" (extension là jpg hoặc png)
    /// Value: Base64 string của ảnh đã crop.
    /// </summary>
    public Dictionary<string, string> Images { get; set; } = new();
}
