using SkiaSharp;
using OCREngine.Models;
using OCREngine.Models.Enum;
using System.Text;
using System.Text.Json;

namespace OCREngine.Helpers;

public static class DotsOcrHelper
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Image Normalization ──

    public static ProcessedImage NormalizeImageToBase64(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("Image file not found.", imagePath);

        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap == null)
            throw new InvalidOperationException("Failed to decode image.");

        using var resized = ImageHelper.ResizeWithMultiple(bitmap, 28);

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

    public static ProcessedImage NormalizeImageToBase64(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            throw new ArgumentNullException(nameof(imageBytes));

        using var bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap == null)
            throw new InvalidOperationException("Failed to decode image.");

        using var scaled = ImageHelper.EnsureMaxDimension(bitmap, 2000);

        using var resized = ImageHelper.ResizeWithMultiple(scaled, 28);

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

    // ── Raw Response Cleaning ──

    public static string CleanRawResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        raw = raw.Trim();

        if (raw.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            raw = raw[7..].TrimStart();
        else if (raw.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            raw = raw[3..].TrimStart();

        if (raw.EndsWith("```"))
            raw = raw[..^3].TrimEnd();

        return raw.Trim();
    }

    // ── LayoutBlock Parsing & Bbox Scaling ──

    public static List<LayoutBlock> ParseLayoutBlocks(
        string jsonContent,
        int originalWidth,
        int originalHeight,
        int processedWidth,
        int processedHeight)
    {
        if (string.IsNullOrWhiteSpace(jsonContent))
            return [];

        var rawBlocks = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(jsonContent, _jsonOptions)
                        ?? throw new JsonException("Failed to deserialize JSON array from model response.");

        var blocks = new List<LayoutBlock>(rawBlocks.Count);

        foreach (var raw in rawBlocks)
        {
            var block = new LayoutBlock();

            if (raw.TryGetValue("bbox", out var bboxEl) && bboxEl.ValueKind == JsonValueKind.Array)
            {
                var coords = JsonSerializer.Deserialize<List<float>>(bboxEl.GetRawText());
                if (coords != null && coords.Count >= 4)
                {
                    block.Bbox = ScaleBboxToOriginal(coords, originalWidth, originalHeight, processedWidth, processedHeight);
                }
            }

            if (raw.TryGetValue("category", out var catEl) && catEl.ValueKind == JsonValueKind.String)
            {
                block.Category = MapCategory(catEl.GetString());
            }

            if (raw.TryGetValue("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                block.Text = textEl.GetString();
            }

            blocks.Add(block);
        }

        return blocks;
    }

    public static List<float> ScaleBboxToOriginal(
        List<float> bbox,
        int originalWidth,
        int originalHeight,
        int processedWidth,
        int processedHeight)
    {
        if (originalWidth <= 0 || originalHeight <= 0 || processedWidth <= 0 || processedHeight <= 0)
            return bbox;

        float scaleX = (float)originalWidth / processedWidth;
        float scaleY = (float)originalHeight / processedHeight;

        return
        [
            bbox[0] * scaleX,
            bbox[1] * scaleY,
            bbox[2] * scaleX,
            bbox[3] * scaleY
        ];
    }

    // ── Category Mapping ──

    private static LayoutCategory? MapCategory(string? category)
    {
        if (string.IsNullOrEmpty(category)) return null;

        return category.ToLower() switch
        {
            "title" => LayoutCategory.Title,
            "text" => LayoutCategory.Text,
            "section-header" => LayoutCategory.SectionHeader,
            "list-item" => LayoutCategory.ListItem,
            "table" => LayoutCategory.Table,
            "formula" => LayoutCategory.Formula,
            "picture" => LayoutCategory.Picture,
            "caption" => LayoutCategory.Caption,
            "footnote" => LayoutCategory.Footnote,
            "page-header" => LayoutCategory.PageHeader,
            "page-footer" => LayoutCategory.PageFooter,
            "figure" => LayoutCategory.Figure,
            _ => LayoutCategory.Text
        };
    }

    // ── Markdown Conversion ──

    public static string ConvertBlocksToMarkdown(List<LayoutBlock> blocks)
    {
        if (blocks == null || blocks.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        foreach (var block in blocks)
        {
            var text = block.Text ?? string.Empty;

            switch (block.Category)
            {
                case LayoutCategory.Formula:
                    sb.AppendLine(FormatFormula(text));
                    break;

                case LayoutCategory.Table:
                    sb.AppendLine(text);
                    break;

                case LayoutCategory.Picture:
                case LayoutCategory.Image:
                case LayoutCategory.Figure:
                    sb.AppendLine(text);
                    break;

                case LayoutCategory.Title:
                case LayoutCategory.SectionHeader:
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine($"## {text.Trim()}");
                    break;

                case LayoutCategory.ListItem:
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine($"- {text.Trim()}");
                    break;

                default:
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine(text.Trim());
                    break;
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatFormula(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = text.Trim();

        if (text.StartsWith("$$") && text.EndsWith("$$"))
            return text;

        if (text.StartsWith("\\[") && text.EndsWith("\\]"))
        {
            var inner = text[2..^2].Trim();
            return $"$$\n{inner}\n$$";
        }

        if (!text.Contains('$') && !text.Contains("\\begin"))
            return $"$$\n{text}\n$$";

        return text;
    }
}
