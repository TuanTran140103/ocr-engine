namespace OCREngine.Models;

public class OcrUploadRequest
{
    public required IFormFile File { get; set; }
    public required string ModelId { get; set; }
}

/// <summary>
/// Request model cho endpoint test OCR 1 ảnh đơn lẻ (synchronous, không qua Hangfire).
/// </summary>
public class OcrTestImageRequest
{
    /// <summary>File ảnh cần OCR (jpg, png, webp, ...).</summary>
    public required IFormFile File { get; set; }

    /// <summary>Model ID để OCR (DeepSeekOcr, ChandraOcr).</summary>
    public required string ModelId { get; set; }

    /// <summary>DPI render — mặc định 300 giống Job.</summary>
    public int TargetDpi { get; set; } = 300;

    /// <summary>Kích thước tối thiểu cạnh ngắn — mặc định 1536 giống Job.</summary>
    public int MinImageDim { get; set; } = 1536;

    /// <summary>Góc xoay ảnh cần test (để thẳng lại ảnh bị nghiêng).</summary>
    public float RotationDegrees { get; set; } = 0;

    /// <summary>Lưu ảnh đã xử lý vào thư mục debug để kiểm tra chất lượng.</summary>
    public bool SaveProcessedImage { get; set; } = true;
    
    /// <summary>Nếu true, dùng ảnh gốc không enhance (bỏ qua resize, rotate, 28-multiple). Mặc định false.</summary>
    public bool UseOriginalImage { get; set; } = false;
    
    public int PageIndex { get; set; } = 0;
}

public class SingleOrientationRequest
{
    public required IFormFile File { get; set; }
}

public class BatchOrientationRequest
{
    public required List<IFormFile> Files { get; set; }
}
