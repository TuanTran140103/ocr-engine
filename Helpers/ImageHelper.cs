using SkiaSharp;
using OCREngine.Models;
using System.Text.RegularExpressions;

namespace OCREngine.Helpers;

/// <summary>
/// Helper class for image processing operations: cropping, rotating, and resizing.
/// This class is self-contained and does not depend on ImageProcessor.
/// </summary>
public static class ImageHelper
{
    #region Cropping

    /// <summary>
    /// Crops a portion of an image from a Base64 string and returns the result as a Base64 string.
    /// </summary>
    public static string CropImageToBase64(string base64Image, int x1, int y1, int x2, int y2)
    {
        if (string.IsNullOrEmpty(base64Image)) return string.Empty;

        try
        {
            byte[] imageBytes = Convert.FromBase64String(base64Image);
            using (var skBitmap = SKBitmap.Decode(imageBytes))
            {
                if (skBitmap == null) return string.Empty;

                using (var cropped = Crop(skBitmap, x1, y1, x2, y2))
                {
                    if (cropped == null) return string.Empty;
                    return EncodeToBase64(cropped, usePng: false, quality: 100);
                }
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Crops a portion of a bitmap. (x1, y1) is top-left, (x2, y2) is bottom-right.
    /// </summary>
    public static SKBitmap? Crop(SKBitmap original, int x1, int y1, int x2, int y2)
    {
        if (original == null) return null;

        // Tạo rect từ 4 điểm OCR trả về (left, top, right, bottom)
        var cropRect = new SKRectI(x1, y1, x2, y2);
        
        // Tạo rect bao quanh toàn bộ ảnh gốc để tính toán phần giao
        var imageRect = new SKRectI(0, 0, original.Width, original.Height);

        // Lấy phần giao để đảm bảo không bị out of bounds (tự động xử lý tọa độ âm hoặc lớn hơn ảnh)
        if (!imageRect.IntersectsWith(cropRect)) return null;

        // Cập nhật imageRect thành phần giao
        imageRect.Intersect(cropRect);

        // imageRect lúc này đã là vùng giao an toàn để crop
        var croppedBitmap = new SKBitmap(imageRect.Width, imageRect.Height);
        
        if (original.ExtractSubset(croppedBitmap, imageRect))
        {
            return croppedBitmap;
        }
        
        croppedBitmap.Dispose();
        return null;
    }

    #endregion

    #region Rotation

    /// <summary>
    /// Rotates a bitmap by specified degrees.
    /// </summary>
    public static SKBitmap Rotate(SKBitmap original, float degrees, SKColor? backgroundColor = null)
    {
        if (original == null) throw new ArgumentNullException(nameof(original));
        
        if (degrees == 0 || degrees == 360) return original.Copy();

        double radians = degrees * Math.PI / 180.0;
        float absCos = Math.Abs((float)Math.Cos(radians));
        float absSin = Math.Abs((float)Math.Sin(radians));

        int newWidth = (int)(original.Width * absCos + original.Height * absSin);
        int newHeight = (int)(original.Width * absSin + original.Height * absCos);

        var rotatedBitmap = new SKBitmap(newWidth, newHeight);

        using (var canvas = new SKCanvas(rotatedBitmap))
        {
            canvas.Clear(backgroundColor ?? SKColors.White);

            canvas.Translate(newWidth / 2f, newHeight / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-original.Width / 2f, -original.Height / 2f);

            using var image = SKImage.FromBitmap(original);
            var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
            canvas.DrawImage(image, 0, 0, sampling);

            return rotatedBitmap;
        }
    }

    public static string RotateImageToBase64(string imagePath, float rotationDegrees, bool usePng = false)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("Image file not found.", imagePath);

        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap == null)
            throw new InvalidOperationException("Failed to decode image.");

        using var rotated = Rotate(bitmap, rotationDegrees);
        return EncodeToBase64(rotated, usePng);
    }

    #endregion

    #region Scaling & Resizing

    /// <summary>
    /// Resizes an image to specified dimensions.
    /// </summary>
    public static SKBitmap Resize(SKBitmap original, int width, int height)
    {
        if (original == null) throw new ArgumentNullException(nameof(original));
        
        var info = new SKImageInfo(width, height, original.ColorType, original.AlphaType);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        
        var resized = original.Resize(info, sampling);
        if (resized == null) throw new InvalidOperationException("Resize failed.");
        
        return resized;
    }

    /// <summary>
    /// Scales an image by a uniform factor.
    /// </summary>
    public static SKBitmap Scale(SKBitmap original, float scale)
    {
        if (original == null) throw new ArgumentNullException(nameof(original));
        if (scale == 1.0f) return original.Copy();
        
        int newWidth = (int)(original.Width * scale);
        int newHeight = (int)(original.Height * scale);
        
        return Resize(original, newWidth, newHeight);
    }

    /// <summary>
    /// Scales an image to ensure a minimum dimension.
    /// </summary>
    public static SKBitmap EnsureMinDimension(SKBitmap original, int minDim)
    {
        if (original == null) throw new ArgumentNullException(nameof(original));

        if (original.Width >= minDim && original.Height >= minDim)
            return original.Copy();

        float scale = (float)minDim / Math.Min(original.Width, original.Height);
        return Scale(original, scale);
    }

    /// <summary>
    /// Scales an image to ensure a maximum dimension constraint.
    /// </summary>
    public static SKBitmap EnsureMaxDimension(SKBitmap original, int maxDim)
    {
        if (original == null) throw new ArgumentNullException(nameof(original));

        if (original.Width <= maxDim && original.Height <= maxDim)
            return original.Copy();

        float scale = (float)maxDim / Math.Max(original.Width, original.Height);
        return Scale(original, scale);
    }

    /// <summary>
    /// Resizes an image ensuring dimensions are multiples of a specific value.
    /// </summary>
    public static SKBitmap ResizeWithMultiple(SKBitmap original, int multiple = 28)
    {
        if (original == null) throw new ArgumentNullException(nameof(original));
        
        int finalWidth = (int)Math.Round(original.Width / (double)multiple) * multiple;
        int finalHeight = (int)Math.Round(original.Height / (double)multiple) * multiple;

        if (finalWidth < multiple) finalWidth = multiple;
        if (finalHeight < multiple) finalHeight = multiple;

        if (finalWidth == original.Width && finalHeight == original.Height) 
            return original.Copy();
        
        return Resize(original, finalWidth, finalHeight);
    }

    #endregion

    #region Decoding & Encoding

    /// <summary>
    /// Decodes a Base64 string to SKBitmap.
    /// </summary>
    /// <param name="base64Image">Base64-encoded image data.</param>
    /// <returns>Decoded SKBitmap.</returns>
    public static SKBitmap DecodeBase64ToBitmap(string base64Image)
    {
        if (string.IsNullOrEmpty(base64Image))
            throw new ArgumentNullException(nameof(base64Image));

        byte[] imageBytes = Convert.FromBase64String(base64Image);
        return DecodeToBitmap(imageBytes);
    }

    /// <summary>
    /// Decodes a byte array to SKBitmap.
    /// </summary>
    /// <param name="imageBytes">Image bytes (JPEG, PNG, etc.).</param>
    /// <returns>Decoded SKBitmap.</returns>
    public static SKBitmap DecodeToBitmap(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            throw new ArgumentNullException(nameof(imageBytes));

        var bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap == null)
            throw new InvalidOperationException("Decode failed: invalid image data.");

        return bitmap;
    }

    /// <summary>
    /// Encodes SKBitmap to Base64 string (PNG or JPEG).
    /// </summary>
    /// <param name="bitmap">Source bitmap to encode.</param>
    /// <param name="usePng">True for PNG, false for JPEG.</param>
    /// <param name="quality">Compression quality (1-100, default: 100).</param>
    /// <returns>Base64-encoded image string.</returns>
    public static string EncodeToBase64(SKBitmap bitmap, bool usePng = false, int quality = 100)
    {
        if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));

        using var image = SKImage.FromBitmap(bitmap);
        var format = usePng ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;

        using var data = image.Encode(format, quality);
        if (data == null) throw new InvalidOperationException("Image encode failed.");

        return Convert.ToBase64String(data.ToArray());
    }

    #endregion

    #region Image Info

    /// <summary>
    /// Gets the width and height of an image from file path.
    /// </summary>
    /// <param name="imagePath">Path to the image file.</param>
    /// <returns>Tuple of (width, height).</returns>
    public static (int Width, int Height) GetImageDimensions(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("Image file not found.", imagePath);

        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap == null)
            throw new InvalidOperationException("Failed to decode image.");

        return (bitmap.Width, bitmap.Height);
    }

    /// <summary>
    /// Gets the width and height of an image from bytes.
    /// </summary>
    /// <param name="imageBytes">Image bytes.</param>
    /// <returns>Tuple of (width, height).</returns>
    public static (int Width, int Height) GetImageDimensions(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            throw new ArgumentNullException(nameof(imageBytes));

        using var bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap == null)
            throw new InvalidOperationException("Failed to decode image.");

        return (bitmap.Width, bitmap.Height);
    }

    /// <summary>
    /// Rotates an image from file and returns Base64 with updated dimensions.
    /// </summary>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="rotationDegrees">Degrees to rotate (positive = clockwise).</param>
    /// <param name="usePng">True for PNG output, false for JPEG.</param>
    /// <returns>Base64-encoded rotated image.</returns>
    

    #endregion
}
