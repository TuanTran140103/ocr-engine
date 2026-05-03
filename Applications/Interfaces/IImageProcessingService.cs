namespace OCREngine.Applications.Interfaces;

/// <summary>
/// Service for preprocessing images before OCR.
/// </summary>
public interface IImageProcessingService
{
    /// <summary>
    /// Processes an image from stream with optional enhancement pipeline.
    /// </summary>
    /// <param name="stream">Input image stream.</param>
    /// <param name="useOriginalImage">If true, skip all enhancements and use original image.</param>
    /// <param name="targetDpi">Target DPI for resizing (default: 200).</param>
    /// <param name="minImageDim">Minimum dimension constraint (default: 28).</param>
    /// <param name="rotationDegrees">Rotation angle in degrees (default: 0).</param>
    /// <param name="usePng">True for PNG output, false for JPEG.</param>
    /// <returns>ProcessedImage with Base64 and dimensions.</returns>
    Task<Models.ProcessedImage> ProcessImageAsync(
        Stream stream,
        bool useOriginalImage,
        int targetDpi = 200,
        int minImageDim = 28,
        float rotationDegrees = 0,
        bool usePng = false);
}
