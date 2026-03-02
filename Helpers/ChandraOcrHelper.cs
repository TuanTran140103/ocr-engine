using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Newtonsoft.Json;
using OCREngine.Models;
using OCREngine.Models.Enum;
using ReverseMarkdown;
namespace OCREngine.Helpers;

public class ChandraOcrHelper
{

    /// <summary>
    /// Sử dụng AngleSharp để Parse Layout và Scale tọa độ
    /// </summary>
    public static async Task<List<LayoutBlock>> ParseLayoutAsync(string cleanedHtml, int imgWidth, int imgHeight, int bboxScaleValue = 1024)
    {
        var result = new List<LayoutBlock>();

        // Sử dụng trực tiếp HtmlParser để parse HTML tĩnh (nhẹ và an toàn hơn)
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(cleanedHtml);

        // Tìm tất cả các div có thuộc tính data-bbox
        var elements = document.QuerySelectorAll("div[data-bbox]");

        float widthScaler = (float)imgWidth / bboxScaleValue;
        float heightScaler = (float)imgHeight / bboxScaleValue;

        foreach (var el in elements)
        {
            var labelRaw = el.GetAttribute("data-label") ?? "Text";
            var bboxRaw = el.GetAttribute("data-bbox");
            var category = MapLabelToCategory(labelRaw);

            List<float>? pixelBbox = null;

            // Chỉ cố gắng parse và scale nếu có tọa độ
            if (!string.IsNullOrEmpty(bboxRaw))
            {
                try
                {
                    var coords = JsonConvert.DeserializeObject<float[]>(bboxRaw);
                    if (coords != null && coords.Length == 4)
                    {
                        pixelBbox = new List<float> {
                            Math.Max(0, coords[0] * widthScaler),
                            Math.Max(0, coords[1] * heightScaler),
                            Math.Min(imgWidth, coords[2] * widthScaler),
                            Math.Min(imgHeight, coords[3] * heightScaler)
                        };
                    }
                }
                catch
                {
                    // Nếu lỗi parse tọa độ, ta vẫn giữ pixelBbox = null 
                    // nhưng KHÔNG bỏ qua element để tránh mất nội dung
                }
            }

            result.Add(new LayoutBlock
            {
                Category = category,
                Text = el.InnerHtml.Trim(),
                Bbox = pixelBbox
            });
        }
        return result;
    }

    private static LayoutCategory MapLabelToCategory(string label)
    {
        return label.ToLower() switch
        {
            "caption" => LayoutCategory.Caption,
            "footnote" => LayoutCategory.Footnote,
            "equation-block" => LayoutCategory.EquationBlock,
            "list-group" => LayoutCategory.ListGroup,
            "page-header" => LayoutCategory.PageHeader,
            "page-footer" => LayoutCategory.PageFooter,
            "image" => LayoutCategory.Image,
            "section-header" => LayoutCategory.SectionHeader,
            "table" => LayoutCategory.Table,
            "text" => LayoutCategory.Text,
            "complex-block" => LayoutCategory.ComplexBlock,
            "code-block" => LayoutCategory.CodeBlock,
            "form" => LayoutCategory.Form,
            "table-of-contents" => LayoutCategory.TableOfContents,
            "figure" => LayoutCategory.Figure,
            _ => LayoutCategory.Text
        };
    }

    public static string ToMarkdown(string html)
    {
        // 1. Cấu hình bộ chuyển đổi tương tự như dự án gốc
        var config = new Config
        {
            UnknownTags = Config.UnknownTagsOption.PassThrough, // Giữ lại các thẻ không biết (như math)
            GithubFlavored = true,
            RemoveComments = true,
            SmartHrefHandling = true
        };
        var converter = new Converter(config);
        string markdown = converter.Convert(html);
        // 2. Xử lý hậu kỳ (Post-processing) cho các thành phần đặc biệt của Chandra

        // Chuyển đổi thẻ <math display="block">...</math> sang $$...$$
        markdown = Regex.Replace(markdown, @"<math[^>]*display=""block""[^>]*>(.*?)</math>",
            m => $"\n$$\n{m.Groups[1].Value.Trim()}\n$$\n", RegexOptions.Singleline);
        // Chuyển đổi thẻ <math>...</math> thông thường sang $...$
        markdown = Regex.Replace(markdown, @"<math[^>]*>(.*?)</math>",
            m => $" ${m.Groups[1].Value.Trim()}$ ", RegexOptions.Singleline);
        // Đảm bảo các bảng biểu (nếu ReverseMarkdown xử lý chưa tốt các bảng phức tạp) 
        // có thể được giữ dưới dạng HTML sạch nếu cần thiết.

        // 4. Xử lý lặp cụm từ/câu (Deduplication cấp độ cao)
        // Chia thành các dòng và lọc bỏ các dòng 'rác' hoặc gần giống nhau
        var lines = markdown.Split(new[] { "\n", "\r\n" }, StringSplitOptions.None);
        var cleanLines = new List<string>();
        string? lastNormalizedLine = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                cleanLines.Add(line);
                continue;
            }

            // Chuẩn hóa dòng để so sánh: viết thường và xóa khoảng trắng
            string normalized = line.ToLowerInvariant().Replace(" ", "");

            // Nếu dòng này cực ngắn (ví dụ rác như &-) hoặc giống hệt dòng trước (kể cả khác hoa/thường)
            if (normalized == lastNormalizedLine || normalized == "&-")
            {
                continue;
            }

            cleanLines.Add(line);
            lastNormalizedLine = normalized;
        }
        markdown = string.Join("\n", cleanLines);

        // 5. Xử lý các thẻ HTML bị lỗi lặp (ví dụ: <td rowspan=<td rowspan=)
        markdown = Regex.Replace(markdown, @"(<[^>]+?)\1{2,}", "$1");

        // 6. Xử lý các ký tự đơn lặp vô nghĩa (...., ----, ----)
        markdown = Regex.Replace(markdown, @"\.{5,}", "...");
        markdown = Regex.Replace(markdown, @"-{5,}", "---");
        markdown = Regex.Replace(markdown, @"_{5,}", "___");

        return markdown.Trim();
    }
    public static string ToMarkdown(List<LayoutBlock> blocks, bool ignoreHeaderFooter = true)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (ignoreHeaderFooter &&
                (block.Category == LayoutCategory.PageHeader || block.Category == LayoutCategory.PageFooter))
            {
                continue;
            }

            // Chuyển đổi nội dụng của block. 
            // Nếu là Ảnh/Picture thì đã là Markdown rồi (từ bước TransformBboxImageToBase64Async), không qua converter nữa để tránh bị escape.
            string markdownContent;
            if (block.Category == LayoutCategory.Picture || block.Category == LayoutCategory.Image || block.Category == LayoutCategory.Figure)
            {
                markdownContent = block.Text ?? "";
            }
            else
            {
                markdownContent = ToMarkdown(block.Text ?? "");
            }

            if (string.IsNullOrWhiteSpace(markdownContent)) continue;

            sb.AppendLine(markdownContent);
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }
}
