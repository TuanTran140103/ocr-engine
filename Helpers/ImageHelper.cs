using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using SkiaSharp;
using OCREngine.Models;
using PDFtoImage;

namespace OCREngine.Helpers;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public class ImageHelper
{
    // pdfium native library: global state is safe after init, but individual
    // document operations are NOT guaranteed thread-safe across versions of the binding.
    // Keep semaphore at (1,1) for render phase only; release BEFORE encode phase.
    private static readonly SemaphoreSlim _pdfiumLock = new SemaphoreSlim(1, 1);

    // -----------------------------------------------------------------------
    // Phase A: pdfium render — protected by semaphore, returns raw SKBitmap.
    // Caller is responsible for disposing the returned SKBitmap.
    // -----------------------------------------------------------------------
    public static async Task<SKBitmap> RenderPdfPageToBitmap(string pdfPath, int pageIndex, int targetDpi = 300)
    {
        await _pdfiumLock.WaitAsync();
        try
        {
            using var stream = File.OpenRead(pdfPath);
            var skBitmap = Conversion.ToImage(stream, pageIndex, options: new RenderOptions { Dpi = targetDpi });
            if (skBitmap == null) throw new InvalidOperationException("Could not render PDF page to SKBitmap.");
            return skBitmap;
        }
        finally
        {
            _pdfiumLock.Release(); // ← semaphore released HERE, before encode
        }
    }

    /// <summary>
    /// Pipeline chuẩn: Nhận Stream file thô (PDF hoặc Image) -> trả về ProcessedImage đã qua bộ lọc (WhiteBG + Multi-28).
    /// Dùng cho Test API để đảm bảo đồng bộ hoàn toàn với Job.
    /// </summary>
    public static async Task<ProcessedImage?> ProcessFileAsync(
        Stream fileStream, string fileName,
        int targetDpi = 300, int minImageDim = 1536, float rotationDegrees = 0, bool usePng = false)
    {
        bool isPdf = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

        if (isPdf)
        {
            // pdfium cần file path, lưu tạm rồi xử lý
            string tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");
            try
            {
                using (var fs = new FileStream(tempPath, FileMode.Create))
                {
                    await fileStream.CopyToAsync(fs);
                }
                return await ProcessPdfPage(tempPath, 0, targetDpi, minImageDim, rotationDegrees, usePng);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
        else
        {
            // Nếu là ảnh, decode và xử lý trực tiếp
            using var skBitmap = SKBitmap.Decode(fileStream);
            if (skBitmap == null) return null;
            return ProcessPdfPage(skBitmap, minImageDim, rotationDegrees, usePng);
        }
    }

    /// <summary>
    /// Single-page: render PDF trang chỉ định -> Process (WhiteBG + Multi-28).
    /// </summary>
    public static async Task<ProcessedImage?> ProcessPdfPage(
        string pdfPath, int pageIndex,
        int targetDpi = 300, int minImageDim = 1536, float rotationDegrees = 0, bool usePng = false)
    {
        using var skBitmap = await RenderPdfPageToBitmap(pdfPath, pageIndex, targetDpi);
        return ProcessPdfPage(skBitmap, minImageDim, rotationDegrees, usePng);
    }

    /// <summary>
    /// Batch-parallel extraction of all pages in a PDF.<br/>
    /// Strategy:<br/>
    ///   • Phase A (pdfium render) is serialised by <c>_pdfiumLock</c> — safe, no native race.<br/>
    ///   • Phase B (resize + JPEG encode + Base64) runs concurrently on the thread-pool.<br/>
    ///   • <paramref name="maxEncodeParallelism"/> caps simultaneous encode workers to avoid
    ///     excessive peak-memory (each page bitmap can be 10-30 MB at DPI 200-300).
    /// </summary>
    public static async Task<ProcessedImage?[]> ProcessAllPdfPagesAsync(
        string pdfPath,
        int totalPages,
        int targetDpi = 300,
        int minImageDim = 1536,
        int maxEncodeParallelism = 4,
        bool usePng = false,
        CancellationToken cancellationToken = default)
    {
        var results = new ProcessedImage?[totalPages];

        using var encodeSem = new SemaphoreSlim(maxEncodeParallelism, maxEncodeParallelism);

        var tasks = Enumerable.Range(0, totalPages).Select(async i =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var skBitmap = await RenderPdfPageToBitmap(pdfPath, i, targetDpi);

            await encodeSem.WaitAsync(cancellationToken);
            try
            {
                results[i] = await Task.Run(
                    () => ProcessPdfPage(skBitmap, minImageDim, usePng: usePng),
                    cancellationToken);
            }
            finally
            {
                encodeSem.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// Core processing logic for an image. Resizes if dimensions are below minimum and handles rotation.
    /// Returns 3-channel RGB (JPEG formatted) on white background.
    /// </summary>
    public static ProcessedImage? ProcessPdfPage(SKBitmap skBitmap, int minImageDim = 1536, float rotationDegrees = 0, bool usePng = false)
    {
        if (skBitmap == null) return null;

        // 1. Phủ nền trắng để loại bỏ kênh Alpha + Xử lý Rotation nếu có
        SKBitmap flattened;
        if (rotationDegrees != 0)
        {
            flattened = RotateByDegrees(skBitmap, rotationDegrees, SKColors.White);
        }
        else
        {
            flattened = FlattenToWhiteBackground(skBitmap);
        }

        using var targetBitmap = flattened;

        int width = targetBitmap.Width;
        int height = targetBitmap.Height;

        int targetWidth = width;
        int targetHeight = height;

        // 2. Đảm bảo kích thước tối thiểu
        if (width < minImageDim || height < minImageDim)
        {
            float scale = (float)minImageDim / Math.Min(width, height);
            targetWidth = (int)(width * scale);
            targetHeight = (int)(height * scale);
        }

        // 3. Quy tắc Bội số 28 (Cực kỳ quan trọng cho DeepSeek-OCR2 / Vision Patches)
        int finalWidth = (int)Math.Round(targetWidth / 28.0) * 28;
        int finalHeight = (int)Math.Round(targetHeight / 28.0) * 28;

        if (finalWidth < 28) finalWidth = 28;
        if (finalHeight < 28) finalHeight = 28;

        string contentType = usePng ? "image/png" : "image/jpeg";
        string base64 = usePng ? EncodeToPngBase64(targetBitmap) : EncodeToJpegBase64(targetBitmap, 100);

        if (finalWidth != width || finalHeight != height)
        {
            var samplingOptions = new SKSamplingOptions(SKCubicResampler.Mitchell);

            using (var resized = targetBitmap.Resize(
                new SKImageInfo(finalWidth, finalHeight, SKImageInfo.PlatformColorType, SKAlphaType.Opaque),
                samplingOptions))
            {
                if (resized == null) throw new InvalidOperationException("Resize failed.");
                return new ProcessedImage
                {
                    Base64 = usePng ? EncodeToPngBase64(resized) : EncodeToJpegBase64(resized, 100),
                    ContentType = contentType,
                    Width = resized.Width,
                    Height = resized.Height
                };
            }
        }

        return new ProcessedImage
        {
            Base64 = base64,
            ContentType = contentType,
            Width = width,
            Height = height
        };
    }

    private static string EncodeToPngBase64(SKBitmap bitmap)
    {
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        {
            if (data == null)
                throw new InvalidOperationException("PNG encode failed.");

            return Convert.ToBase64String(data.ToArray());
        }
    }

    private static string EncodeToJpegBase64(SKBitmap bitmap, int quality = 100)
    {
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Jpeg, quality))
        {
            if (data == null)
                throw new InvalidOperationException("JPEG encode failed.");

            return Convert.ToBase64String(data.ToArray());
        }
    }


    public static string CropImageToBase64(string base64Image, int x1, int y1, int x2, int y2)
    {
        byte[] imageBytes = Convert.FromBase64String(base64Image);
        using (var skBitmap = SKBitmap.Decode(imageBytes))
        {
            if (skBitmap == null) throw new InvalidOperationException("Could not decode image for cropping.");

            int x = Math.Max(0, x1);
            int y = Math.Max(0, y1);
            int width = Math.Min(skBitmap.Width - x, x2 - x1);
            int height = Math.Min(skBitmap.Height - y, y2 - y1);

            if (width <= 0 || height <= 0) return string.Empty;

            var cropRect = new SKRectI(x, y, x + width, y + height);
            using (var croppedBitmap = new SKBitmap(cropRect.Width, cropRect.Height))
            {
                skBitmap.ExtractSubset(croppedBitmap, cropRect);
                return EncodeToJpegBase64(croppedBitmap, 100);
            }
        }
    }

    public static int GetPageCount(string pdfPath)
    {
        if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath))
            return 0;

        using var stream = File.OpenRead(pdfPath);
        return Conversion.GetPageCount(stream);
    }

    public static SKBitmap RotateByDegrees(SKBitmap original, float degrees, SKColor? backgroundColor = null)
    {
        if (original == null)
            throw new ArgumentNullException(nameof(original));

        double radians = degrees * Math.PI / 180.0;
        float absCos = Math.Abs((float)Math.Cos(radians));
        float absSin = Math.Abs((float)Math.Sin(radians));

        int newWidth = (int)(original.Width * absCos + original.Height * absSin);
        int newHeight = (int)(original.Width * absSin + original.Height * absCos);

        var rotatedBitmap = new SKBitmap(newWidth, newHeight);

        using (var surface = new SKCanvas(rotatedBitmap))
        {
            surface.Clear(backgroundColor ?? SKColors.Transparent);

            surface.Translate(newWidth / 2f, newHeight / 2f);
            surface.RotateDegrees(degrees);
            surface.Translate(-original.Width / 2f, -original.Height / 2f);

            using var image = SKImage.FromBitmap(original);

            var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);

            surface.DrawImage(image, 0, 0, sampling);


            return rotatedBitmap;
        }
    }

    private static SKBitmap FlattenToWhiteBackground(SKBitmap original)
    {
        // Sử dụng PlatformColorType và Opaque để loại bỏ hoàn toàn kênh Alpha
        var info = new SKImageInfo(original.Width, original.Height, SKImageInfo.PlatformColorType, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(original, 0, 0);
        }
        return bitmap;
    }
}
