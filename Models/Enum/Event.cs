
namespace OCREngine.Models.Enum;

public enum EventType
{
    Logging,
    SaveLog,
    GetMarkdown
}

public enum EventStatus
{
    Started,
    Processing,
    Successed,
    Failed,
    Canceled
}
