using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Options;
using OCREngine.Applications.Interfaces;
using OCREngine.Options;
using OCREngine.Utils;

namespace OCREngine.Infrastructure.Services;

public class S3Service : IS3Service
{
    private readonly IAmazonS3 _s3Client;

    public S3Service(IOptions<S3Options> options)
    {
        var s3Opts = options.Value;

        var s3Config = new AmazonS3Config
        {
            ServiceURL = s3Opts.ServiceUrl,
            ForcePathStyle = s3Opts.ForcePathStyle,
            AuthenticationRegion = s3Opts.Region,
            UseHttp = s3Opts.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        };

        var credentials = new BasicAWSCredentials(s3Opts.AccessKey, s3Opts.SecretKey);
        _s3Client = new AmazonS3Client(credentials, s3Config);
    }

    public async Task<string> DownloadFileAsync(string bucket, string objectKey, string taskId, CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(objectKey);
        var localPath = Path.Combine(FileUtil.GetTempUploadPath(), $"{taskId}_{fileName}");

        var request = new GetObjectRequest
        {
            BucketName = bucket,
            Key = objectKey
        };

        using var response = await _s3Client.GetObjectAsync(request, cancellationToken);
        await using var fileStream = new FileStream(localPath, FileMode.Create);
        await response.ResponseStream.CopyToAsync(fileStream, cancellationToken);

        return localPath;
    }
}
