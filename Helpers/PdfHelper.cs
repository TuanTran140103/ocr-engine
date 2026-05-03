using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace OCREngine.Helpers;

/// <summary>
/// Helper class for PDF operations using Docnet.Core.
/// Thread-safe through DocLib's internal locking mechanism.
/// </summary>
public static class PdfHelper
{
    /// <summary>
    /// System-wide semaphore: limits concurrent PDF rendering to 2 jobs at any time
    /// across all Hangfire workers to prevent OOM under heavy load.
    /// </summary>
    private static readonly SemaphoreSlim _renderSemaphore = new(2, 2);
    /// <summary>
    /// Renders a PDF page to SKBitmap (RGB format, white background).
    /// </summary>
    /// <param name="pdfPath">Path to the PDF file.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="targetDpi">Target DPI for rendering (default: 200).</param>
    /// <returns>SKBitmap with white background (no alpha).</returns>
    public static async Task<SKBitmap> RenderPdfPageAsync(string pdfPath, int pageIndex, int targetDpi = 200)
    {
        return await Task.Run(() =>
        {
            var scale = targetDpi / 72.0f;

            using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(scale));
            using var pageReader = docReader.GetPageReader(pageIndex);

            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();
            var rawBytes = pageReader.GetImage();

            // Create bitmap with Premul alpha (raw bytes from PDF have alpha)
            var bitmap = CreateBitmapFromBgraWithAlpha(rawBytes, width, height);
            
            // Flatten to white background
            var flattened = FlattenToWhiteBackground(bitmap);
            bitmap.Dispose();
            
            return flattened;
        });
    }

    /// <summary>
    /// Renders a PDF page to SKBitmap from a stream (RGB format, white background).
    /// </summary>
    public static async Task<SKBitmap> RenderPdfPageAsync(Stream pdfStream, int pageIndex, int targetDpi = 200)
    {
        return await Task.Run(() =>
        {
            using var memoryStream = new MemoryStream();
            pdfStream.CopyTo(memoryStream);
            var bytes = memoryStream.ToArray();

            var scale = targetDpi / 72.0f;

            using var docReader = DocLib.Instance.GetDocReader(bytes, new PageDimensions(scale));
            using var pageReader = docReader.GetPageReader(pageIndex);

            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();
            var rawBytes = pageReader.GetImage();

            var bitmap = CreateBitmapFromBgraWithAlpha(rawBytes, width, height);
            var flattened = FlattenToWhiteBackground(bitmap);
            bitmap.Dispose();
            
            return flattened;
        });
    }

    /// <summary>
    /// Renders a PDF page with specific dimensions to SKBitmap (white background).
    /// </summary>
    public static async Task<SkiaSharp.SKBitmap> RenderPdfPageAsync(string pdfPath, int pageIndex, PageDimensions dimensions)
    {
        return await Task.Run(() =>
        {
            using var docReader = DocLib.Instance.GetDocReader(pdfPath, dimensions);
            using var pageReader = docReader.GetPageReader(pageIndex);

            var rawBytes = pageReader.GetImage();
            var bitmap = CreateBitmapFromBgraWithAlpha(rawBytes, dimensions.DimOne, dimensions.DimTwo);
            var flattened = FlattenToWhiteBackground(bitmap);
            bitmap.Dispose();
            
            return flattened;
        });
    }

    /// <summary>
    /// Renders all pages of a PDF and saves them as images to a local directory.
    /// Optimized with Task.WhenAll for true parallel processing and batch handling to avoid OOM.
    /// </summary>
    /// <param name="pdfPath">Path to the PDF file.</param>
    /// <param name="outputDir">Directory to save the rendered images.</param>
    /// <param name="targetDpi">Target DPI for rendering (default: 200).</param>
    /// <param name="format">Image format ("jpeg" or "png", default: "jpeg").</param>
    /// <param name="quality">Compression quality (default: 95).</param>
    /// <param name="batchSize">Number of pages to process in parallel per batch (default: 100).</param>
    /// <returns>A list of absolute paths to the saved images.</returns>
    public static async Task<List<string>> RenderPdfToLocalAsync(
        string pdfPath,
        string outputDir,
        int targetDpi = 200,
        string format = "jpeg",
        int quality = 95,
        int batchSize = 100)
    {
        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // Acquire the global render slot — at most 2 RenderPdfToLocalAsync calls run concurrently
        // across the entire server, regardless of how many Hangfire jobs are active.
        await _renderSemaphore.WaitAsync();
        try
        {
            var scale = targetDpi / 72.0f;
            var encodedFormat = format.ToLower() == "png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
            var extension = format.ToLower() == "png" ? "png" : "jpeg";

            using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(scale));
            var pageCount = docReader.GetPageCount();

            var savedPaths = new string[pageCount];

            // Process pages internally in batches to avoid allocating too much memory at once.
            for (int start = 0; start < pageCount; start += batchSize)
            {
                var end = Math.Min(start + batchSize, pageCount);
                var batchTasks = new List<Task>(end - start);

                for (int i = start; i < end; i++)
                {
                    var pageIndex = i;
                    batchTasks.Add(Task.Run(() =>
                    {
                        using var pageReader = docReader.GetPageReader(pageIndex);
                        var width = pageReader.GetPageWidth();
                        var height = pageReader.GetPageHeight();
                        var rawBytes = pageReader.GetImage();

                        using var bitmap = CreateBitmapFromBgraWithAlpha(rawBytes, width, height);
                        using var flattened = FlattenToWhiteBackground(bitmap);
                        using var image = SKImage.FromBitmap(flattened);
                        using var data = image.Encode(encodedFormat, quality);

                        var fileName = $"page_{pageIndex}.{extension}";
                        var filePath = Path.Combine(outputDir, fileName);

                        File.WriteAllBytes(filePath, data.ToArray());
                        savedPaths[pageIndex] = Path.GetFullPath(filePath);
                    }));
                }

                await Task.WhenAll(batchTasks);
            }

            return savedPaths.ToList();
        }
        finally
        {
            _renderSemaphore.Release();
        }
    }

    /// <summary>
    /// Gets the page count of a PDF file.
    /// </summary>
    /// <param name="pdfPath">Path to the PDF file.</param>
    /// <returns>Number of pages in the PDF.</returns>
    public static async Task<int> GetPageCountAsync(string pdfPath)
    {
        return await Task.Run(() =>
        {
            using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1.0));
            return docReader.GetPageCount();
        });
    }

    /// <summary>
    /// Gets the page count of a PDF from a stream.
    /// </summary>
    /// <param name="pdfStream">Stream containing the PDF data.</param>
    /// <returns>Number of pages in the PDF.</returns>
    public static async Task<int> GetPageCountAsync(Stream pdfStream)
    {
        return await Task.Run(() =>
        {
            using var memoryStream = new MemoryStream();
            pdfStream.CopyTo(memoryStream);
            var bytes = memoryStream.ToArray();

            using var docReader = DocLib.Instance.GetDocReader(bytes, new PageDimensions(1.0));
            return docReader.GetPageCount();
        });
    }

    /// <summary>
    /// Gets the dimensions of a PDF page.
    /// </summary>
    /// <param name="pdfPath">Path to the PDF file.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <returns>Page width and height.</returns>
    public static async Task<(int Width, int Height)> GetPageDimensionsAsync(string pdfPath, int pageIndex)
    {
        return await Task.Run(() =>
        {
            using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1.0));
            using var pageReader = docReader.GetPageReader(pageIndex);
            return (pageReader.GetPageWidth(), pageReader.GetPageHeight());
        });
    }

    /// <summary>
    /// Renders a thumbnail image for orientation detection.
    /// Fixed: maxDim=1500, dpi=150, quality=100, jpeg format.
    /// </summary>
    /// <param name="pdfPath">Path to the PDF file.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <returns>Encoded JPEG image bytes.</returns>
    public static async Task<byte[]> RenderThumbnailAsync(
        string pdfPath,
        int pageIndex)
    {
        return await Task.Run(() =>
        {
            const int maxDim = 1500;
            const int renderDpi = 150;
            const int quality = 100;

            // Step 1: Get original dimensions
            using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1.0));
            using var pageReader = docReader.GetPageReader(pageIndex);

            var originalWidth = pageReader.GetPageWidth();
            var originalHeight = pageReader.GetPageHeight();

            // Step 2: Calculate uniform scale for target DPI and maxDim constraint
            float dpiScale = renderDpi / 72.0f;
            float maxDimScale = 1.0f;
            if (originalWidth * dpiScale > maxDim || originalHeight * dpiScale > maxDim)
            {
                maxDimScale = Math.Min(
                    (float)maxDim / (originalWidth * dpiScale),
                    (float)maxDim / (originalHeight * dpiScale)
                );
            }
            float scale = dpiScale * maxDimScale;

            // Step 3: Render with calculated scale (avoids PageDimensions dimOne <= dimTwo constraint for landscape pages)
            using var scaledDocReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(scale));
            using var scaledPageReader = scaledDocReader.GetPageReader(pageIndex);
            var renderWidth = scaledPageReader.GetPageWidth();
            var renderHeight = scaledPageReader.GetPageHeight();
            var rawBytes = scaledPageReader.GetImage();

            // Step 4: Wrap BGRA bytes into SKBitmap with alpha, then flatten to white background
            using var bitmap = CreateBitmapFromBgraWithAlpha(rawBytes, renderWidth, renderHeight);
            using var flattened = FlattenToWhiteBackground(bitmap);
            using var image = SkiaSharp.SKImage.FromBitmap(flattened);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        });
    }

    /// <summary>
    /// Renders thumbnail images for all pages in a PDF file.
    /// Fixed: maxDim=1500, dpi=150, quality=100, jpeg format.
    /// Optimized with batch processing to avoid OOM.
    /// </summary>
    /// <param name="pdfPath">Path to the PDF file.</param>
    /// <param name="outputDir">Directory to save the thumbnail images (null = return bytes only).</param>
    /// <param name="batchSize">Number of pages to process in parallel per batch (default: 50).</param>
    /// <returns>A list of JPEG-encoded image bytes for each page (in order).</returns>
    public static async Task<List<byte[]>> RenderAllThumbnailsAsync(
        string pdfPath,
        string? outputDir = null,
        int batchSize = 50)
    {
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        const int maxDim = 1500;
        const int renderDpi = 150;
        const int quality = 100;

        // Acquire global render semaphore — giới hạn concurrent PDFium calls
        // giống RenderPdfToLocalAsync, tránh crash native khi nhiều job chạy song song
        await _renderSemaphore.WaitAsync();
        try
        {
            // Lấy pageCount bằng reader riêng biệt, đóng ngay sau khi xong
            int pageCount;
            using (var countReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1.0)))
            {
                pageCount = countReader.GetPageCount();
            }

            var results = new byte[pageCount][];

            // Process in batches to avoid OOM
            for (int start = 0; start < pageCount; start += batchSize)
            {
                var end = Math.Min(start + batchSize, pageCount);
                var batchTasks = new List<Task>(end - start);

                for (int i = start; i < end; i++)
                {
                    var pageIndex = i;
                    batchTasks.Add(Task.Run(() =>
                    {
                        // Mỗi task tạo docReader riêng — tránh race condition
                        using var metaReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1.0));
                        using var pageReader = metaReader.GetPageReader(pageIndex);

                        var originalWidth = pageReader.GetPageWidth();
                        var originalHeight = pageReader.GetPageHeight();

                        float dpiScale = renderDpi / 72.0f;
                        float maxDimScale = 1.0f;
                        if (originalWidth * dpiScale > maxDim || originalHeight * dpiScale > maxDim)
                        {
                            maxDimScale = Math.Min(
                                (float)maxDim / (originalWidth * dpiScale),
                                (float)maxDim / (originalHeight * dpiScale)
                            );
                        }
                        float scale = dpiScale * maxDimScale;

                        using var scaledDocReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(scale));
                        using var scaledPageReader = scaledDocReader.GetPageReader(pageIndex);
                        var renderWidth = scaledPageReader.GetPageWidth();
                        var renderHeight = scaledPageReader.GetPageHeight();
                        var rawBytes = scaledPageReader.GetImage();

                        using var bitmap = CreateBitmapFromBgraWithAlpha(rawBytes, renderWidth, renderHeight);
                        using var flattened = FlattenToWhiteBackground(bitmap);
                        using var image = SKImage.FromBitmap(flattened);
                        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);

                        var imageData = data.ToArray();
                        results[pageIndex] = imageData;

                        if (!string.IsNullOrEmpty(outputDir))
                        {
                            var fileName = $"thumbnail_{pageIndex}.jpg";
                            var filePath = Path.Combine(outputDir, fileName);
                            File.WriteAllBytes(filePath, imageData);
                        }
                    }));
                }

                await Task.WhenAll(batchTasks);
            }

            return results.ToList();
        }
        finally
        {
            _renderSemaphore.Release();
        }
    }

    #region Private Helpers

    /// <summary>
    /// Wraps BGRA bytes (with alpha) into an SKBitmap using InstallPixels + GCHandle.
    /// Uses SKAlphaType.Premul to correctly handle alpha channel from PDF.
    /// </summary>
    private static SKBitmap CreateBitmapFromBgraWithAlpha(byte[] bgraBytes, int width, int height)
    {
        // Dùng Marshal.Copy để SAO CHÉP bytes vào SKBitmap's own memory.
        // Tránh InstallPixels+GCHandle vì bitmap chỉ wrap pointer tới Docnet's buffer —
        // khi sk_image_new_from_bitmap truy cập pixel data dưới concurrent load → crash 0xC0000005.
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);

        var pixelPtr = bitmap.GetPixels();
        Marshal.Copy(bgraBytes, 0, pixelPtr, bgraBytes.Length);

        return bitmap;
    }

    /// <summary>
    /// Flattens a bitmap onto a white background to remove Alpha channel.
    /// Prevents black background issue when PDF has transparent areas.
    /// </summary>
    private static SKBitmap FlattenToWhiteBackground(SKBitmap original)
    {
        var info = new SKImageInfo(original.Width, original.Height, SKImageInfo.PlatformColorType, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(original, 0, 0);
        }
        return bitmap;
    }

    #endregion
}
