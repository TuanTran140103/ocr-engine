using OCREngine.Models;

namespace OCREngine.Applications.Interfaces;

public interface IDocOriService
{
    Task<OrientationResult> PredictAsync(string filePath, CancellationToken cancellationToken = default);
    Task<OrientationResult> PredictAsync(byte[] imageBytes, string fileName, CancellationToken cancellationToken = default);
    Task<BatchOrientationResult> PredictBatchAsync(List<string> filePaths, CancellationToken cancellationToken = default);
    Task<BatchOrientationResult> PredictBatchAsync(List<(byte[] Bytes, string FileName)> images, CancellationToken cancellationToken = default);
}
