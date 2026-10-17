namespace OCREngine.Applications.Interfaces;

public interface IS3Service
{
    Task<string> DownloadFileAsync(string bucket, string objectKey, string taskId, CancellationToken cancellationToken = default);
}
