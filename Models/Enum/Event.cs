
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
    Succeeded,
    Failed,
    Canceled
}
