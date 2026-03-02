using System.Text;
using System.Text.RegularExpressions;
using OCREngine.Models;
using OCREngine.Models.Enum;
using System.Collections.Generic;
using System.Text.Json;

namespace OCREngine.Helpers;

public class DotsOcrHelper
{
    /// <summary>
    /// Bước 1: Sửa lỗi chuỗi JSON thô từ LLM trả về (ví dụ: thiếu dấu phẩy giữa các {}, hoặc bị cắt cụt)
    /// </summary>
    public static string CleanRawResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "[]";
        text = text.Trim();

        // 1. Nếu AI trả về định dạng bọc trong ```json ... ``` thì lấy phần lõi
        var codeBlockMatch = Regex.Match(text, @"```json\s*(.*?)\s*```", RegexOptions.Singleline);
        if (codeBlockMatch.Success) text = codeBlockMatch.Groups[1].Value;

        // 2. Cơ chế mới: Quét theo điểm đánh dấu "bbox"
        var matches = Regex.Matches(text, @"""bbox""\s*:\s*\[");
        if (matches.Count == 0) return text.StartsWith("[") ? text : "[" + text + "]";

        var blocks = new List<LayoutBlock>();
        for (int i = 0; i < matches.Count; i++)
        {
            int startPos = matches[i].Index;
            int endPos = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;

            string segment = text.Substring(startPos, endPos - startPos).Trim();
            LayoutBlock? block = RecoverBlockFromSegment(segment);
            if (block != null) blocks.Add(block);
        }

        // 3. Logic dọn dẹp (vừa được chuyển từ CleanOutputAsync sang)
        if (blocks.Count == 0) return "[]";

        var finalBlocks = new List<LayoutBlock>();
        var categoryTextPairs = new Dictionary<(LayoutCategory?, string), int>();
        var bboxPairs = new HashSet<string>();

        foreach (var block in blocks)
        {
            // Lọc Bbox: 3 điểm -> null, 4 điểm -> giữ, còn lại giữ nếu có text/category
            if (block.Bbox != null)
            {
                if (block.Bbox.Count == 3) block.Bbox = null;
                else if (block.Bbox.Count != 4) block.Bbox = null;
            }

            // Deduplication by Text + Category (threshold 10)
            var key = (block.Category, block.Text ?? "");
            if (!categoryTextPairs.ContainsKey(key)) categoryTextPairs[key] = 0;
            categoryTextPairs[key]++;
            if (categoryTextPairs[key] > 10) continue;

            // Deduplication by Bbox
            if (block.Bbox != null && block.Bbox.Count == 4)
            {
                var bboxKey = string.Join(",", block.Bbox);
                if (bboxPairs.Contains(bboxKey)) continue;
                bboxPairs.Add(bboxKey);
            }

            finalBlocks.Add(block);
        }

        return JsonSerializer.Serialize(finalBlocks, new JsonSerializerOptions { WriteIndented = false });
    }

    private static LayoutBlock? RecoverBlockFromSegment(string segment)
    {
        try
        {
            // 1. Tìm thông tin BBOX
            var bboxNumbers = new List<float>();
            int firstBracket = segment.IndexOf('[');
            int textStartPos = 0;

            if (firstBracket != -1)
            {
                var matches = Regex.Matches(segment.Substring(firstBracket), @"\d+");
                var letterMatch = Regex.Match(segment.Substring(firstBracket), @"[a-zA-Z\u00C0-\u1EF9]");
                int firstLetterInBracket = letterMatch.Success ? letterMatch.Index + firstBracket : int.MaxValue;

                foreach (Match m in matches)
                {
                    int absoluteIdx = m.Index + firstBracket;
                    if (absoluteIdx >= firstLetterInBracket) break;

                    if (float.TryParse(m.Value, out float val))
                    {
                        bboxNumbers.Add(val);
                        textStartPos = absoluteIdx + m.Length;
                    }
                    if (bboxNumbers.Count >= 4) break;
                }
            }

            // 2. Tìm Category
            LayoutCategory category = LayoutCategory.Text;
            var catMatch = Regex.Match(segment, @"""category""\s*:\s*""([^""]+)""");
            if (catMatch.Success)
            {
                if (Enum.TryParse<LayoutCategory>(catMatch.Groups[1].Value, true, out var parsedCat))
                    category = parsedCat;
            }

            // 3. Tìm Text
            string textValue = "";
            var textMatch = Regex.Match(segment, @"""text""\s*:\s*""((?:[^""\\]|\\.)*)""?");
            if (textMatch.Success && !string.IsNullOrWhiteSpace(textMatch.Groups[1].Value))
            {
                textValue = textMatch.Groups[1].Value;
            }
            else
            {
                textValue = segment.Substring(Math.Min(textStartPos, segment.Length));
                textValue = Regex.Replace(textValue, @"""category""\s*:\s*""[^""]*""?,?", "", RegexOptions.IgnoreCase);
                textValue = Regex.Replace(textValue, @"""text""\s*:\s*""?,?", "", RegexOptions.IgnoreCase);
                textValue = Regex.Replace(textValue, @"""bbox""\s*:\s*\[?,?", "", RegexOptions.IgnoreCase);
                textValue = textValue.Trim(' ', ',', '{', '}', '\r', '\n', '\t', '"', ':', '[', ']', ')', '(', '，');
            }

            if (textValue.EndsWith("\"")) textValue = textValue.Substring(0, textValue.Length - 1);
            textValue = textValue.Replace("\\\"", "\""); // Đưa về nguyên bản, Serialize sẽ lo phần escape

            return new LayoutBlock
            {
                Bbox = bboxNumbers.Count > 0 ? bboxNumbers : null,
                Category = category,
                Text = textValue
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Bước 2: Làm sạch văn bản và định dạng công thức toán học
    /// </summary>
    public static string FormatContent(LayoutBlock block)
    {
        if (string.IsNullOrEmpty(block.Text)) return "";
        string text = block.Text.Trim();
        switch (block.Category)
        {
            case LayoutCategory.Formula:
                return WrapLatexFormula(text);

            case LayoutCategory.Table:
                // Thường table là HTML, giữ nguyên hoặc xử lý thêm nếu cần
                return text;
            case LayoutCategory.Picture:
                return text;
            default:
                // Xử lý text chung: loại bỏ các chuỗi lặp lại như ........ hoặc ________
                // 1. Collapse long character repetitions (e.g., 5+ dots or underscores)
                text = Regex.Replace(text, @"\.{5,}", "...");
                text = Regex.Replace(text, @"_{5,}", "___");
                text = Regex.Replace(text, @"-{5,}", "---");

                // 2. Collapse any character repeated 10+ times (catch-all for other symptoms of loops)
                text = Regex.Replace(text, @"(.)\1{9,}", "$1$1$1");

                // 3. Collapse short sequence repetitions (e.g. "abcabcabc...")
                text = Regex.Replace(text, @"(.{2,})\1{4,}", "$1$1$1");

                return text;
        }
    }
    /// <summary>
    /// Helper xử lý LaTeX để hiển thị tốt trên Markdown
    /// </summary>
    private static string WrapLatexFormula(string latex)
    {
        // Nếu chưa có dấu $$ thì bao bọc lại
        if (!latex.StartsWith("$$") && !latex.EndsWith("$$"))
        {
            return $"\n$$\n{latex}\n$$\n";
        }
        return latex;
    }
    /// <summary>
    /// Bước 3: Chuyển đổi danh sách Blocks sang Markdown hoàn chỉnh
    /// </summary>
    public static string ToMarkdown(List<LayoutBlock> blocks, bool ignoreHeaderFooter = true)
    {
        StringBuilder sb = new StringBuilder();
        foreach (var block in blocks)
        {
            // Bỏ qua Header/Footer nếu cần (thường dùng khi gộp trang)
            if (ignoreHeaderFooter &&
                (block.Category == LayoutCategory.PageHeader || block.Category == LayoutCategory.PageFooter))
            {
                continue;
            }
            string formattedText = FormatContent(block);

            if (string.IsNullOrWhiteSpace(formattedText)) continue;

            sb.AppendLine(formattedText);
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }
    /// <summary>
    /// Helper: Scale tọa độ bbox về kích thước thực tế của ảnh/trang PDF
    /// </summary>
    public static List<int>? ScaleBbox(List<int> originalBbox, double scaleX, double scaleY)
    {
        if (originalBbox == null || originalBbox.Count != 4) return originalBbox;
        return new List<int>
        {
            (int)(originalBbox[0] * scaleX),
            (int)(originalBbox[1] * scaleY),
            (int)(originalBbox[2] * scaleX),
            (int)(originalBbox[3] * scaleY)
        };
    }
}