namespace OCREngine.Prompts;

public class DotsOcrPrompt
{
    public static string PromptLayoutAllEn = @"Please output the layout information from the PDF image, including each layout element's bbox, its category, and the corresponding text content within the bbox.
1. Bbox format: [x1, y1, x2, y2]
2. Layout Categories: The possible categories are ['Caption', 'Footnote', 'Formula', 'List-item', 'Page-footer', 'Page-header', 'Picture', 'Section-header', 'Table', 'Text', 'Title'].
3. Text Extraction & Formatting Rules:
    - Picture: For the 'Picture' category, the text field should be omitted.
    - Formula: Format its text as LaTeX.
    - Table: Format its text as HTML.
    - All Others (Text, Title, etc.): Format their text as Markdown.
4. Constraints:
    - The output text must be the original text from the image, with no translation.
    - All layout elements must be sorted according to human reading order.
5. Final Output: The entire output must be a single JSON object.
";
    /// <summary>
    /// Layout detection only.
    /// </summary>
    public static string PromptLayoutOnlyEn = "Please output the layout information from this PDF image, including each layout's bbox and its category. The bbox should be in the format [x1, y1, x2, y2]. The layout categories for the PDF document include ['Caption', 'Footnote', 'Formula', 'List-item', 'Page-footer', 'Page-header', 'Picture', 'Section-header', 'Table', 'Text', 'Title']. Do not output the corresponding text. The layout result should be in JSON format.";
    /// <summary>
    /// Simple OCR text extraction.
    /// </summary>
    public static string PromptOcr = "Extract the text content from this image.";
    /// <summary>
    /// Extract text content in the given bounding box.
    /// </summary>
    public static string PromptGroundingOcr = @"Extract text from the given bounding box on the image (format: [x1, y1, x2, y2]).
Bounding Box:
";
    // Dictionary để map mode sang prompt tương tự như Python (nếu cần)
    public static readonly Dictionary<string, string> DictPromptModeToPrompt =
        new Dictionary<string, string>
    {
        { "prompt_layout_all_en", PromptLayoutAllEn },
        { "prompt_layout_only_en", PromptLayoutOnlyEn },
        { "prompt_ocr", PromptOcr },
        { "prompt_grounding_ocr", PromptGroundingOcr }
    };
}