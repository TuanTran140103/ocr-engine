using Microsoft.Extensions.Logging;

namespace OCREngine.Applications.Jobs;

/// <summary>
/// Manages temporary resources for an OCR job and ensures cleanup.
/// Implements IAsyncDisposable for automatic cleanup using await using.
/// </summary>
public sealed class OcrJobResources : IAsyncDisposable
{
    private readonly string _taskId;
    private readonly string? _pdfPath;
    private readonly ILogger _logger;
    private bool _disposed = false;

    public OcrJobResources(string taskId, string? pdfPath, ILogger logger)
    {
        _taskId = taskId;
        _pdfPath = pdfPath;
        _logger = logger;
    }

    /// <summary>
    /// Cleans up all temporary resources:
    /// 1. tmp/ocr_images/{taskId}/ — rendered page images from RenderPdfToLocalAsync
    /// 2. tmp_upload/{taskId}_*.pdf — the original uploaded PDF file
    /// 3. tmp/job_mapping/mapping.json — the taskId→jobId mapping entry
    /// Called automatically via "await using" even on exceptions.
    /// NOTE: native process crashes (0xC0000005) bypass this — handled via startup cleanup in Program.cs.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        // 1. Xóa thư mục ảnh render (tmp/ocr_images/{taskId}/)
        try
        {
            Utils.FileUtil.CleanupJobTempDir(_taskId);
            _logger.LogInformation("[CLEANUP] Task {TaskId} — deleted rendered image dir (tmp/ocr_images/{TaskId})", _taskId, _taskId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CLEANUP] Task {TaskId} — failed to delete rendered image dir", _taskId);
        }

        // 2. Xóa file PDF upload gốc (tmp_upload/{taskId}_*.pdf)
        if (!string.IsNullOrEmpty(_pdfPath) && File.Exists(_pdfPath))
        {
            try
            {
                File.Delete(_pdfPath);
                _logger.LogInformation("[CLEANUP] Task {TaskId} — deleted temp PDF: {Path}", _taskId, _pdfPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CLEANUP] Task {TaskId} — failed to delete temp PDF: {Path}", _taskId, _pdfPath);
            }
        }

        // 3. Xóa job mapping
        await Utils.FileUtil.DeleteJobMapping(_taskId);
        _logger.LogInformation("[CLEANUP] Task {TaskId} — removed job mapping", _taskId);

        _disposed = true;
        await Task.CompletedTask;
    }
}
