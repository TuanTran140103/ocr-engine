using System.Text.Json;
using System.Text.RegularExpressions;
using OCREngine.Models;
using OCREngine.Models.Enum;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;

namespace OCREngine.Helpers;

public static class DeepSeekOcrHelper
{
    private const int TileSize = 768;
    private const int GlobalSize = 1024;

    #region Tiling Logic (DeepSeek-OCR2)

    public static (int Cols, int Rows) CalculateBestGrid(int w, int h)
    {
        var targetRatios = new (int Cols, int Rows)[]
        {
            (1, 2), (2, 1), (1, 3), (3, 1), (2, 2), (2, 3), (3, 2),
            (1, 4), (4, 1), (1, 5), (5, 1), (1, 6), (6, 1)
        };

        float aspectRatio = (float)w / h;
        (int Cols, int Rows) bestGrid = (1, 1);
        float minError = float.MaxValue;

        foreach (var grid in targetRatios)
        {
            float gridRatio = (float)grid.Cols / grid.Rows;
            float error = Math.Abs(aspectRatio - gridRatio);

            if (error < minError)
            {
                minError = error;
                bestGrid = grid;
            }
            else if (Math.Abs(error - minError) < 0.0001f)
            {
                // Tie-breaker: pick larger grid if image area > 50% of that grid's total area
                int currentGridArea = bestGrid.Cols * bestGrid.Rows;
                int newGridArea = grid.Cols * grid.Rows;

                if (newGridArea > currentGridArea)
                {
                    float gridTotalPixelArea = newGridArea * TileSize * TileSize;
                    if ((float)w * h > 0.5f * gridTotalPixelArea)
                    {
                        bestGrid = grid;
                    }
                }
            }
        }

        return bestGrid;
    }

    public static Image<Rgb24> GetGlobalView(Image sourceImg)
    {
        var img = sourceImg.CloneAs<Rgb24>();
        img.Mutate(x => x.AutoOrient());

        var result = new Image<Rgb24>(GlobalSize, GlobalSize);
        result.Mutate(x => x.Fill(Color.FromRgb(127, 127, 127))); // Gray padding per reference

        float scale = Math.Min((float)GlobalSize / img.Width, (float)GlobalSize / img.Height);
        int targetWidth = (int)(img.Width * scale);
        int targetHeight = (int)(img.Height * scale);

        img.Mutate(x => x.Resize(targetWidth, targetHeight));

        int xOffset = (GlobalSize - targetWidth) / 2;
        int yOffset = (GlobalSize - targetHeight) / 2;

        result.Mutate(x => x.DrawImage(img, new Point(xOffset, yOffset), 1f));

        return result;
    }

    public static List<Image<Rgb24>> GetLocalTiles(Image sourceImg)
    {
        var img = sourceImg.CloneAs<Rgb24>();
        img.Mutate(x => x.AutoOrient());

        var grid = CalculateBestGrid(img.Width, img.Height);
        int targetWidth = grid.Cols * TileSize;
        int targetHeight = grid.Rows * TileSize;

        img.Mutate(x => x.Resize(targetWidth, targetHeight));

        var tiles = new List<Image<Rgb24>>();
        for (int r = 0; r < grid.Rows; r++)
        {
            for (int c = 0; c < grid.Cols; c++)
            {
                var tile = img.Clone(x => x.Crop(new Rectangle(c * TileSize, r * TileSize, TileSize, TileSize)));
                tiles.Add(tile.CloneAs<Rgb24>());
            }
        }

        return tiles;
    }

    public static Image<Rgb24> GetCombinedView(Image sourceImg)
    {
        var grid = CalculateBestGrid(sourceImg.Width, sourceImg.Height);
        using var globalView = GetGlobalView(sourceImg);
        var localTiles = GetLocalTiles(sourceImg);

        int combinedWidth = Math.Max(GlobalSize, grid.Cols * TileSize);
        int combinedHeight = GlobalSize + (grid.Rows * TileSize);

        var combinedImg = new Image<Rgb24>(combinedWidth, combinedHeight);
        combinedImg.Mutate(x => x.Fill(Color.White));

        // 1. Draw Global View (centered horizontally at the top)
        int globalXOffset = (combinedWidth - GlobalSize) / 2;
        combinedImg.Mutate(x => x.DrawImage(globalView, new Point(globalXOffset, 0), 1f));

        // 2. Draw Local Tiles
        int tileGroupWidth = grid.Cols * TileSize;
        int tileGroupXOffset = (combinedWidth - tileGroupWidth) / 2;

        for (int r = 0; r < grid.Rows; r++)
        {
            for (int c = 0; c < grid.Cols; c++)
            {
                int index = r * grid.Cols + c;
                if (index < localTiles.Count)
                {
                    using var tile = localTiles[index];
                    int x = tileGroupXOffset + (c * TileSize);
                    int y = GlobalSize + (r * TileSize);
                    combinedImg.Mutate(ctx => ctx.DrawImage(tile, new Point(x, y), 1f));
                }
            }
        }

        return combinedImg;
    }

    public static string ImageToBase64(Image img)
    {
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 100 });
        return Convert.ToBase64String(ms.ToArray());
    }

    #endregion

    #region Layout Parsing Logic

    public static List<float> UnionBboxes(List<List<float>> bboxes)
    {
        if (bboxes == null || bboxes.Count == 0)
            return new List<float> { 0, 0, 0, 0 };
        float minX = bboxes.Min(b => b[0]);
        float minY = bboxes.Min(b => b[1]);
        float maxX = bboxes.Max(b => b[2]);
        float maxY = bboxes.Max(b => b[3]);
        return new List<float> { minX, minY, maxX, maxY };
    }

    /// <summary>
    /// Parse văn bản raw chứa các tag <|ref|>...<|det|>... thành danh sách LayoutBlock.
    /// Ogni LayoutBlock sẽ chỉ chứa duy nhất một Bbox (đã được Union).
    /// </summary>
    public static List<LayoutBlock> ParseLayoutBlocks(string rawText)
    {
        var result = new List<LayoutBlock>();

        // Regex bắt thẻ Tag
        var tagRegex = new Regex(@"(<\|ref\|>(.*?)<\|/ref\|><\|det\|>(.*?)<\|/det\|>)", RegexOptions.Singleline);
        var matches = tagRegex.Matches(rawText);
        for (int i = 0; i < matches.Count; i++)
        {
            var currentMatch = matches[i];
            string categoryName = currentMatch.Groups[2].Value.Trim();
            string detJson = currentMatch.Groups[3].Value.Trim();
            // XÁC ĐỊNH VĂN BẢN THUỘC VỀ TAG NÀY:
            // Lấy từ vị trí kết thúc của Tag này đến vị trí bắt đầu của Tag kế tiếp
            int startOfText = currentMatch.Index + currentMatch.Length;
            int endOfText = (i + 1 < matches.Count) ? matches[i + 1].Index : rawText.Length;

            string ocrText = rawText.Substring(startOfText, endOfText - startOfText).Trim();
            try
            {
                var listOfBoxes = JsonSerializer.Deserialize<List<List<float>>>(detJson);
                if (listOfBoxes != null && listOfBoxes.Count > 0)
                {
                    var mergedBbox = UnionBboxes(listOfBoxes);
                    result.Add(new LayoutBlock
                    {
                        Category = ConvertLabelToCategory(categoryName),
                        Bbox = mergedBbox,
                        Text = ocrText
                    });
                }
            }
            catch { }
        }
        return result;
    }
    private static LayoutCategory ConvertLabelToCategory(string label)
    {
        if (string.IsNullOrEmpty(label)) return LayoutCategory.Text;

        return label.ToLower() switch
        {
            "title" or "section-header" or "sub_title" => LayoutCategory.Title,
            "table" or "grid" => LayoutCategory.Table,
            "image" or "figure" or "picture" => LayoutCategory.Image,
            _ => LayoutCategory.Text,
        };
    }

    /// <summary>
    /// Scale tọa độ từ hệ 999 (Model) sang Pixel thực (Ảnh gốc).
    /// </summary>
    public static List<float> ScaleToReal(List<float> bbox, int imgWidth, int imgHeight)
    {
        if (bbox == null || bbox.Count != 4)
            return new List<float> { 0, 0, 0, 0 };

        // Công thức: (Tọa độ / 999) * Kích thước thật
        float x1 = (bbox[0] / 999.0f) * imgWidth;
        float y1 = (bbox[1] / 999.0f) * imgHeight;
        float x2 = (bbox[2] / 999.0f) * imgWidth;
        float y2 = (bbox[3] / 999.0f) * imgHeight;

        // Ràng buộc để không vượt quá biên ảnh
        return new List<float>
        {
            Math.Max(0, x1),
            Math.Max(0, y1),
            Math.Min(imgWidth, x2),
            Math.Min(imgHeight, y2)
        };
    }

    #endregion
}
