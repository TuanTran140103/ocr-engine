using System.Text.Json.Serialization;

namespace OCREngine.Models.Enum;

public enum LayoutCategory
{
    Caption,

    [JsonStringEnumMemberName("Code-block")]
    CodeBlock,

    [JsonStringEnumMemberName("Complex-block")]
    ComplexBlock,

    [JsonStringEnumMemberName("Equation-block")]
    EquationBlock,

    Figure,
    Footnote,
    Form,
    Formula,
    Image,

    [JsonStringEnumMemberName("List-group")]
    ListGroup,

    [JsonStringEnumMemberName("List-item")]
    ListItem,

    [JsonStringEnumMemberName("Page-footer")]
    PageFooter,

    [JsonStringEnumMemberName("Page-header")]
    PageHeader,

    Picture,

    [JsonStringEnumMemberName("Section-header")]
    SectionHeader,

    Table,

    [JsonStringEnumMemberName("Table-of-contents")]
    TableOfContents,

    Text,
    Title
}