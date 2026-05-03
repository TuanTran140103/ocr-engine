using SkiaSharp;
using OCREngine.Models;

namespace OCREngine.Helpers;

/// <summary>
/// Helper class for Dots OCR image preprocessing.
/// </summary>
public static class DotsOcrHelper
{
    /// <summary>
    /// Reads an image from path, normalizes it to a multiple of 28, and returns Base64 with dimensions.
    /// </summary>
    /// <param name="imagePath">Path to the image file (rendered from PDF at 200 DPI).</param>
    /// <returns>ProcessedImage containing Base64, width, and height.</returns>
    public static ProcessedImage NormalizeImageToBase64(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("Image file not found.", imagePath);

        // Decode image to SKBitmap
        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap == null)
            throw new InvalidOperationException("Failed to decode image.");

        // Resize to multiple of 28
        using var resized = ImageHelper.ResizeWithMultiple(bitmap, 28);

        // Encode to Base64 (JPEG, quality 100)
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var base64 = Convert.ToBase64String(data.ToArray());

        return new ProcessedImage
        {
            Base64 = base64,
            ContentType = "image/jpeg",
            Width = resized.Width,
            Height = resized.Height
        };
    }

    /// <summary>
    /// Reads an image from bytes, normalizes it (max 1500px, multiple of 28), and returns Base64 with dimensions.
    /// </summary>
    /// <param name="imageBytes">Image bytes (rendered from PDF at 200 DPI).</param>
    /// <returns>ProcessedImage containing Base64, width, and height.</returns>
    public static ProcessedImage NormalizeImageToBase64(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            throw new ArgumentNullException(nameof(imageBytes));

        // Decode image to SKBitmap
        using var bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap == null)
            throw new InvalidOperationException("Failed to decode image.");

        // Step 1: Ensure max dimension constraint (1500px)
        using var scaled = ImageHelper.EnsureMaxDimension(bitmap, 1500);

        // Step 2: Resize to multiple of 28
        using var resized = ImageHelper.ResizeWithMultiple(scaled, 28);

        // Encode to Base64 (JPEG, quality 100)
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var base64 = Convert.ToBase64String(data.ToArray());

        return new ProcessedImage
        {
            Base64 = base64,
            ContentType = "image/jpeg",
            Width = resized.Width,
            Height = resized.Height
        };
    }
}
