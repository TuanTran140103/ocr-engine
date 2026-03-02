using Microsoft.Extensions.Logging;

namespace OCREngine.Utils;

public static class FileUtil
{
    public static string GetOutputDir()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "Outputs");
    }

    /// <summary>
    /// Tìm file markdown (.md) trong thư mục Outputs dựa theo taskId.
    /// Tên file có định dạng: {FileName}_{TaskId}_{Timestamp}.md
    /// </summary>
    /// <param name="taskId">Mã Task ID cần tìm.</param>
    /// <returns>Đường dẫn tuyệt đối tới file nếu tìm thấy, ngược lại trả về null.</returns>
    public static string? GetMarkdownFilePath(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        if (!Directory.Exists(GetOutputDir()))
        {
            return null;
        }

        // Tìm kiếm file markdown có chứa taskId trong tên
        // Pattern: *_{taskId}_*.md
        // Lưu ý: taskId nên đủ unique để tránh trùng lặp substring
        string searchPattern = $"*_{taskId}_*.md";

        var files = Directory.GetFiles(GetOutputDir(), searchPattern);

        if (files.Length == 0)
        {
            return null;
        }

        // Nếu có nhiều file (do chạy lại nhiều lần?), lấy file mới nhất theo thời gian tạo
        // Hoặc thời gian ghi cuối cùng
        var latestFile = files
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();

        return latestFile?.FullName;
    }
}
