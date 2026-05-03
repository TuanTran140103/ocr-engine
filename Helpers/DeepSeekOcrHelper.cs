using System.Text.Json;
using System.Text.RegularExpressions;
using OCREngine.Models;
using OCREngine.Models.Enum;

namespace OCREngine.Helpers;

public static class DeepSeekOcrHelper
{
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

            string ocrText = rawText.Substring(startOfText, endOfText - startOfText);

            // Chuẩn hóa text: loại bỏ leading/trailing whitespace nhưng giữ internal formatting
            ocrText = NormalizeBlockText(ocrText);

            if (!string.IsNullOrEmpty(ocrText))
            {
                try
                {
                    var listOfBoxes = JsonSerializer.Deserialize<List<List<float>>>(detJson);
                    if (listOfBoxes != null && listOfBoxes.Count > 0)
                    {
                        var mergedBbox = UnionBboxes(listOfBoxes);
                        var categoryNameMapped = ConvertLabelToCategory(categoryName);
                        if(categoryNameMapped == LayoutCategory.Title && ocrText.Contains("tài liệu public", StringComparison.OrdinalIgnoreCase))
                        {
                            categoryNameMapped = LayoutCategory.Text;
                            ocrText = ocrText.TrimStart('#');
                        }
                        result.Add(new LayoutBlock
                        {
                            Category = categoryNameMapped,
                            Bbox = mergedBbox,
                            Text = ocrText
                        });
                    }
                }
                catch { }
            }
        }
        return result;
    }

    /// <summary>
    /// Chuẩn hóa text cho block: loại bỏ whitespace thừa nhưng giữ nguyên formatting nội bộ.
    /// </summary>
    private static string NormalizeBlockText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // Trim leading/trailing whitespace
        text = text.Trim();

        // Loại bỏ các dòng trống ở đầu và cuối
        var lines = text.Split('\n');
        
        // Tìm dòng đầu tiên không rỗng
        int firstNonEmpty = 0;
        while (firstNonEmpty < lines.Length && string.IsNullOrWhiteSpace(lines[firstNonEmpty]))
            firstNonEmpty++;

        // Tìm dòng cuối cùng không rỗng
        int lastNonEmpty = lines.Length - 1;
        while (lastNonEmpty >= 0 && string.IsNullOrWhiteSpace(lines[lastNonEmpty]))
            lastNonEmpty--;

        if (firstNonEmpty > lastNonEmpty)
            return string.Empty;

        // Lấy các dòng từ first đến last, giữ nguyên internal formatting
        var trimmedLines = lines.Skip(firstNonEmpty).Take(lastNonEmpty - firstNonEmpty + 1);
        return string.Join('\n', trimmedLines).Trim();
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


    private static List<float> UnionBboxes(List<List<float>> bboxes)
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
    /// Scale bbox từ tọa độ grid 999x999 về tọa độ thực tế của ảnh.
    /// DeepSeek-OCR trả về bbox trên grid chuẩn 999x999, cần scale theo kích thước thật của ảnh.
    /// </summary>
    public static List<float> ScaleToReal(List<float> bbox, int imageWidth, int imageHeight)
    {
        if (bbox == null || bbox.Count < 4)
            return bbox ?? new List<float>();

        // DeepSeek trả về bbox dạng [x1, y1, x2, y2] trên grid 999x999
        // Scale về kích thước thật của ảnh
        float scaleX = imageWidth / 999f;
        float scaleY = imageHeight / 999f;

        return new List<float>
        {
            bbox[0] * scaleX,
            bbox[1] * scaleY,
            bbox[2] * scaleX,
            bbox[3] * scaleY
        };
    }
}