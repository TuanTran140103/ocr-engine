using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Dom;
using OCREngine.Models;
using OCREngine.Models.Enum;
using AngleSharp;

namespace OCREngine.Helpers;

/// <summary>
/// Helper class for parsing and processing ChandraOCR response.
/// Parses HTML output with data-bbox and data-label attributes into LayoutBlocks.
/// </summary>
public static class ChandraOcrHelper
{
    /// <summary>
    /// Parses raw ChandraOCR response text (HTML format) into a list of LayoutBlocks.
    /// Expects HTML with div elements containing data-bbox and data-label attributes.
    /// Bboxes are automatically scaled from normalized (0-1000) to real image coordinates.
    /// </summary>
    public static List<LayoutBlock> ParseLayoutBlocks(string rawText, int imageWidth, int imageHeight)
    {
        var result = new List<LayoutBlock>();

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return result;
        }

        // Tạo parser và load HTML
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = context.OpenAsync(req => req.Content(rawText)).GetAwaiter().GetResult();

        // Tìm tất cả các thẻ div có data-bbox HOẶC data-label
        var divElements = document.QuerySelectorAll("div[data-bbox], div[data-label]");

        foreach (var div in divElements)
        {
            var block = ParseDiv(div, imageWidth, imageHeight);
            if (block != null)
            {
                result.Add(block);
            }
        }

        return result;
    }

    /// <summary>
    /// Parse một thẻ div thành LayoutBlock
    /// </summary>
    private static LayoutBlock? ParseDiv(IElement div, int imageWidth, int imageHeight)
    {
        var bboxAttr = div.GetAttribute("data-bbox");
        var labelAttr = div.GetAttribute("data-label");

        // Nếu không có cả 2 thuộc tính thì bỏ qua
        if (string.IsNullOrWhiteSpace(bboxAttr) && string.IsNullOrWhiteSpace(labelAttr))
        {
            return null;
        }

        // Parse bbox từ chuỗi (format: "[x1,y1,x2,y2]" hoặc "x1,y1,x2,y2")
        // và scale về tọa độ thực của ảnh
        var bbox = ParseAndScaleBbox(bboxAttr ?? string.Empty, imageWidth, imageHeight);

        // Parse category từ data-label
        var category = ParseCategory(labelAttr ?? string.Empty);

        // Lấy toàn bộ nội dung HTML bên trong div (bao gồm các thẻ con)
        var text = div.InnerHtml?.Trim() ?? string.Empty;

        return new LayoutBlock
        {
            Bbox = bbox,
            Category = category,
            Text = text
        };
    }

    /// <summary>
    /// Parse chuỗi bbox thành List<float> và scale về tọa độ thực của ảnh.
    /// </summary>
    /// <param name="bboxStr">Chuỗi bbox (format: "x1,y1,x2,y2" hoặc "[x1,y1,x2,y2]")</param>
    /// <param name="imageWidth">Chiều rộng ảnh gốc</param>
    /// <param name="imageHeight">Chiều cao ảnh gốc</param>
    /// <returns>Bbox đã scale về tọa độ thực, hoặc null nếu parse failed</returns>
    public static List<float>? ParseAndScaleBbox(string bboxStr, int imageWidth, int imageHeight)
    {
        var rawBbox = ParseBbox(bboxStr);
        if (rawBbox == null || rawBbox.Count < 4)
            return null;

        return ScaleToReal(rawBbox, imageWidth, imageHeight);
    }

    /// <summary>
    /// Parse chuỗi bbox thành List<float>
    /// Hỗ trợ các format: "[x1,y1,x2,y2]", "x1 y1 x2 y2", "x1,y1,x2,y2"
    /// </summary>
    private static List<float>? ParseBbox(string bboxStr)
    {
        if (string.IsNullOrWhiteSpace(bboxStr))
        {
            return null;
        }

        // Loại bỏ dấu [] nếu có
        var cleanStr = bboxStr.Trim('[', ']', ' ');

        // Tách bằng dấu phẩy hoặc khoảng trắng
        var separators = new[] { ',', ' ' };
        var parts = cleanStr.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 4)
        {
            return null;
        }

        var bbox = new List<float>();
        foreach (var part in parts.Take(4))
        {
            if (float.TryParse(part, out var value))
            {
                bbox.Add(value);
            }
            else
            {
                return null;
            }
        }

        return bbox;
    }

    /// <summary>
    /// Parse chuỗi label thành LayoutCategory enum
    /// </summary>
    private static LayoutCategory? ParseCategory(string labelStr)
    {
        if (string.IsNullOrWhiteSpace(labelStr))
        {
            return null;
        }

        var label = labelStr.Trim();

        // Ưu tiên convert các label đặc biệt trước (form -> Table, ...)
        var converted = ConvertLabelToCategory(label);
        if (converted.HasValue)
        {
            return converted.Value;
        }
        return LayoutCategory.Text;
    }

    /// <summary>
    /// Convert các label đặc biệt sang LayoutCategory
    /// </summary>
    private static LayoutCategory? ConvertLabelToCategory(string label)
    {
        if (string.IsNullOrEmpty(label)) return null;

        return label.ToLower() switch
        {
            "title" or "section-header" or "sub_title" or "subtitle" => LayoutCategory.Title,
            "table" or "grid" or "form" => LayoutCategory.Table,
            "image" or "picture" => LayoutCategory.Image,
            "figure" or "fig" or "diagram" => LayoutCategory.Figure,
            "code-block" or "codeblock" or "code" => LayoutCategory.CodeBlock,
            "list-item" or "listitem" or "list" => LayoutCategory.ListItem,
            "list-group" or "listgroup" => LayoutCategory.ListGroup,
            "page-header" or "pageheader" or "header" => LayoutCategory.PageHeader,
            "page-footer" or "pagefooter" or "footer" => LayoutCategory.PageFooter,
            "equation-block" or "equationblock" or "equation" or "formula" => LayoutCategory.EquationBlock,
            "complex-block" or "complexblock" => LayoutCategory.ComplexBlock,
            "caption" or "description" => LayoutCategory.Caption,
            "footnote" or "note" => LayoutCategory.Footnote,
            // "form" or "input" => LayoutCategory.Form,
            _ => LayoutCategory.Text
        };
    }

    /// <summary>
    /// Scales bbox from normalized coordinates to actual image dimensions.
    /// </summary>
    public static List<float> ScaleToReal(List<float> bbox, int imageWidth, int imageHeight)
    {
        if (bbox == null || bbox.Count < 4)
            return bbox!;

        float scaleX = imageWidth / 1000f;
        float scaleY = imageHeight / 1000f;

        return new List<float>
        {
            bbox[0] * scaleX,
            bbox[1] * scaleY,
            bbox[2] * scaleX,
            bbox[3] * scaleY
        };
    }

    /// <summary>
    /// Chuyển đổi danh sách LayoutBlock từ HTML sang Markdown.
    /// Riêng Table, Figure, Image, Picture sẽ không được convert (giữ nguyên HTML).
    /// </summary>
    public static string ConvertBlocksToMarkdown(List<LayoutBlock> blocks)
    {
        if (blocks == null || blocks.Count == 0)
        {
            return string.Empty;
        }

        var markdown = new System.Text.StringBuilder();

        // Các category giữ nguyên HTML, không convert sang Markdown
        var skipConvertCategories = new[] { 
            LayoutCategory.Table, LayoutCategory.Figure, 
            LayoutCategory.Image, LayoutCategory.Picture,
        };

        foreach (var block in blocks)
        {
            if (skipConvertCategories.Any(c => c == block.Category))
            {
                markdown.AppendLine(block.Text ?? string.Empty);
            }
            else
            {
                // Convert HTML sang Markdown
                var md = ConvertHtmlToMarkdown(block.Text ?? string.Empty);
                // Remove escape characters (\) trước các ký tự đặc biệt
                md = UnescapeMarkdown(md);
                markdown.AppendLine(md);
            }

            markdown.AppendLine();
        }

        return markdown.ToString();
    }

    /// <summary>
    /// Chuyển đổi một chuỗi HTML sang Markdown
    /// </summary>
    private static string ConvertHtmlToMarkdown(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return html ?? string.Empty;
        }

        var converter = new ReverseMarkdown.Converter();
        return converter.Convert(html);
    }

    /// <summary>
    /// Loại bỏ escape character (\) trước các ký tự không cần thiết
    /// </summary>
    private static string UnescapeMarkdown(string md)
    {
        if (string.IsNullOrEmpty(md)) return md;

        // Remove \ trước các ký tự đặc biệt không cần escape trong ngữ cảnh thông thường
        // (), _, `, #, -, +, >, [, ]
        md = Regex.Replace(md, @"\\([()_`\#\[\]+->])", "$1");

        return md;
    }

    /// <summary>
    /// Loại bỏ caption/text thừa SAU thẻ &lt;img&gt; trong Diagram blocks.
    /// Giữ nguyên text TRƯỚC &lt;img&gt; (text overlay trên diagram) và chính thẻ &lt;img&gt;.
    /// </summary>
    public static async Task<string> CleanDiagramBlockTextAsync(string htmlContent)
    {
        if (string.IsNullOrEmpty(htmlContent))
            return htmlContent;

        try
        {
            var parser = new HtmlParser();
            var document = await parser.ParseDocumentAsync($"<div>{htmlContent}</div>");
            var rootDiv = document.Body!.QuerySelector("div")!;

            // Tìm thẻ <img> đầu tiên
            var firstImg = rootDiv.QuerySelector("img");
            if (firstImg == null)
                return htmlContent; // Không có img thì giữ nguyên

            // Xóa tất cả node con SAU thẻ <img>
            bool foundImg = false;
            var children = rootDiv.ChildNodes.ToArray();
            foreach (var child in children)
            {
                if (ReferenceEquals(child, firstImg))
                {
                    foundImg = true;
                    continue;
                }

                // Chỉ xóa node SAU <img>, giữ nguyên node TRƯỚC <img>
                if (foundImg && child.NodeName.ToLower() != "img")
                {
                    child.Parent?.RemoveChild(child);
                }
            }

            return rootDiv.InnerHtml;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error cleaning diagram block: {ex.Message}");
            return htmlContent; // Fallback: giữ nguyên nếu lỗi
        }
    }

    /// <summary>
    /// Cập nhật thuộc tính src cho thẻ img trong HTML content.
    /// Nếu img đã có src, thay thế bằng bboxKey.
    /// Nếu img chưa có src, thêm src với bboxKey.
    /// </summary>
    public static async Task<string> UpdateImgSrcAttributeAsync(string htmlContent, string bboxKey)
    {
        if (string.IsNullOrEmpty(htmlContent))
            return htmlContent;

        try
        {
            var parser = new HtmlParser();
            var document = await parser.ParseDocumentAsync($"<div>{htmlContent}</div>");
            var rootDiv = document.Body!.QuerySelector("div")!;

            var imgElements = rootDiv.QuerySelectorAll("img");
            foreach (var img in imgElements)
            {
                img.SetAttribute("src", bboxKey);
            }

            return rootDiv.InnerHtml;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error updating img src attribute: {ex.Message}");
            return htmlContent;
        }
    }

    /// <summary>
    /// Xử lý HTML trong Table blocks: loại bỏ data-bbox khỏi các thẻ table, td, tr, th...
    /// và giữ nguyên data-bbox cho thẻ img, đồng thời thêm src attribute với bbox key.
    /// </summary>
    /// <param name="tableBlock">LayoutBlock chứa HTML table</param>
    /// <param name="base64Image">Ảnh gốc dạng base64 để crop</param>
    /// <param name="imageWidth">Chiều rộng ảnh gốc (để scale bbox)</param>
    /// <param name="imageHeight">Chiều cao ảnh gốc (để scale bbox)</param>
    /// <returns>Dictionary: key là bbox key, value là base64 ảnh đã crop</returns>
    public static async Task<Dictionary<string, string>> ExtractImagesFromTableBlocks(
        LayoutBlock tableBlock,
        string base64Image,
        int imageWidth,
        int imageHeight)
    {
        // Default implementation: dùng ImageHelper.CropImageToBase64 thật
        return await ExtractImagesFromTableBlocksInternal(
            tableBlock,
            base64Image,
            imageWidth,
            imageHeight,
            (base64, x1, y1, x2, y2) => ImageHelper.CropImageToBase64(base64, x1, y1, x2, y2));
    }

    /// <summary>
    /// Internal method để test có thể mock crop function
    /// </summary>
    internal static async Task<Dictionary<string, string>> ExtractImagesFromTableBlocksInternal(
        LayoutBlock tableBlock,
        string base64Image,
        int imageWidth,
        int imageHeight,
        Func<string, int, int, int, int, string> cropFunc)
    {
        var images = new Dictionary<string, string>();

        if (tableBlock?.Text == null)
            return images;

        try
        {
            var parser = new HtmlParser();
            var document = await parser.ParseDocumentAsync($"<div>{tableBlock.Text}</div>");
            var rootDiv = document.Body!.QuerySelector("div")!;

            // 1. Loại bỏ data-bbox khỏi các thẻ không phải img
            var tagsToRemoveBbox = new[] { "table", "td", "tr", "th", "tbody", "thead", "tfoot", "div", "p", "span" };

            foreach (var tagName in tagsToRemoveBbox)
            {
                var elements = rootDiv.QuerySelectorAll(tagName);
                foreach (var el in elements)
                {
                    el.RemoveAttribute("data-bbox");
                }
            }

            // 2. Xử lý các thẻ img: giữ data-bbox và thêm src attribute
            var imgElements = rootDiv.QuerySelectorAll("img");
            foreach (var img in imgElements)
            {
                var dataBbox = img.GetAttribute("data-bbox");
                if (!string.IsNullOrEmpty(dataBbox))
                {
                    // Parse bbox (raw coordinates)
                    var rawBbox = ParseBbox(dataBbox);
                    if (rawBbox != null && rawBbox.Count >= 4)
                    {
                        // Scale bbox về tọa độ thực của ảnh
                        var scaledBbox = ScaleToReal(rawBbox, imageWidth, imageHeight);

                        // Crop ảnh từ bbox đã scale
                        var croppedBase64 = cropFunc(
                            base64Image,
                            (int)scaledBbox[0],
                            (int)scaledBbox[1],
                            (int)scaledBbox[2],
                            (int)scaledBbox[3]);

                        if (!string.IsNullOrEmpty(croppedBase64))
                        {
                            // Tạo bbox key từ tọa độ đã scale
                            string bboxKey = $"{(int)scaledBbox[0]}_{(int)scaledBbox[1]}_{(int)scaledBbox[2]}_{(int)scaledBbox[3]}.jpg";

                            // Thêm vào dictionary
                            images[bboxKey] = croppedBase64;

                            // Set src attribute trỏ vào bbox key
                            img.SetAttribute("src", bboxKey);
                        }
                    }
                }
            }

            // 3. Cập nhật lại Text của block với HTML đã xử lý
            tableBlock.Text = rootDiv.InnerHtml;
        }
        catch (Exception ex)
        {
            // Log error nhưng không làm fail cả process
            System.Diagnostics.Debug.WriteLine($"Error extracting images from table: {ex.Message}");
        }

        return images;
    }
}
