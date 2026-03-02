using System;
using System.Collections.Generic;

namespace OCREngine.Prompts;

public static class ChandraOcrPrompt
{
    // 1. Danh sách các Tag và Attribute được phép (Dùng string.Join để tránh lỗi format)
    private static readonly string AllowedTags = "[math, br, i, b, u, del, sup, sub, table, tr, td, p, th, div, pre, h1, h2, h3, h4, h5, ul, ol, li, input, a, span, img, hr, tbody, small, caption, strong, thead, big, code]";
    private static readonly string AllowedAttributes = "[class, colspan, rowspan, display, checked, type, border, value, style, href, alt, align]";

    // 2. Phần chỉ dẫn cuối cùng (Guidelines) - Dùng @"" và Escape dấu ngoặc nhọn
    public static readonly string PromptEnding = $@"
Only use these tags {AllowedTags}, and these attributes {AllowedAttributes}.

Guidelines:
* Inline math: Surround math with <math>...</math> tags. Math expressions should be rendered in KaTeX-compatible LaTeX. Use display for block math.
* Tables: Use colspan and rowspan attributes to match table structure.
* Formatting: Maintain consistent formatting with the image, including spacing, indentation, subscripts/superscripts, and special characters.
* Images: Include a description of any images in the alt attribute of an <img> tag. Do not fill out the src property.
* Forms: Mark checkboxes and radio buttons properly.
* Text: join lines together properly into paragraphs using <p>...</p> tags.  Use <br> tags for line breaks within paragraphs, but only when absolutely necessary to maintain meaning.
* Use the simplest possible HTML structure that accurately represents the content of the block.
* Make sure the text is accurate and easy for a human to read and interpret.  Reading order should be correct and natural.".Trim();

    // 3. Prompt Layout (Bao gồm Bounding Box)
    // Lưu ý: Cú pháp {{bbox_scale}} ở đây là cách C# escape dấu ngoặc để in ra chuỗi {bbox_scale}
    public static string GetOcrLayoutPrompt(int bboxScale = 1024)
    {
        return $@"
OCR this image to HTML, arranged as layout blocks.  Each layout block should be a div with the data-bbox attribute representing the bounding box of the block in [x0, y0, x1, y1] format.  Bboxes are normalized 0-{bboxScale}. The data-label attribute is the label for the block.

Use the following labels:
- Caption
- Footnote
- Equation-Block
- List-Group
- Page-Header
- Page-Footer
- Image
- Section-Header
- Table
- Text
- Complex-Block
- Code-Block
- Form
- Table-Of-Contents
- Figure

{PromptEnding}".Trim();
    }

    // 4. Prompt OCR thường (Chỉ lấy HTML)
    public static readonly string OcrPrompt = $@"
OCR this image to HTML.

{PromptEnding}".Trim();

    // 5. Mapping tiện ích để sử dụng giống như trong Python
    public static string GetPrompt(string type = "ocr_layout", int bboxScale = 1024)
    {
        return type.ToLower() switch
        {
            "ocr_layout" => GetOcrLayoutPrompt(bboxScale),
            "ocr" => OcrPrompt,
            _ => GetOcrLayoutPrompt(bboxScale)
        };
    }
}

