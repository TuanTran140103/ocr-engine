using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace OCREngine.Utils;

public static class FileUtil
{
    // Base path: current working directory (project root when running, /app in Docker)
    private static readonly string _basePath = Directory.GetCurrentDirectory();
    private static readonly SemaphoreSlim _mappingSemaphore = new(1, 1);

    private const string JOB_MAPPING_FILE = "tmp/job_mapping/mapping.json";

    // Temporary directory names
    public const string TEMP_UPLOAD_DIR = "tmp_upload";
    public const string DEBUG_DIR = "tmp_debug";

    /// <summary>
    /// Gets the temporary upload directory path, creating it if it doesn't exist.
    /// </summary>
    public static string GetTempUploadPath()
    {
        var path = Path.Combine(_basePath, TEMP_UPLOAD_DIR);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }

    /// <summary>
    /// Gets the debug directory path, creating it if it doesn't exist.
    /// </summary>
    public static string GetDebugPath()
    {
        var path = Path.Combine(_basePath, DEBUG_DIR);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }

    /// <summary>
    /// Saves an uploaded file to the temporary upload directory.
    /// </summary>
    /// <param name="file">The uploaded file.</param>
    /// <param name="taskId">Task ID to prefix the filename.</param>
    /// <param name="originalFileName">Original filename.</param>
    /// <returns>Full path to the saved file.</returns>
    public static async Task<string> SaveTempUploadFileAsync(IFormFile file, string taskId, string originalFileName)
    {
        var uploadPath = GetTempUploadPath();
        var filePath = Path.Combine(uploadPath, $"{taskId}_{originalFileName}");

        await using var stream = new FileStream(filePath, FileMode.Create);
        await file.CopyToAsync(stream);

        return filePath;
    }

    /// <summary>
    /// Saves raw model response content to debug directory for testing.
    /// Only active in Development environment.
    /// File format: Markdown with token info header.
    /// </summary>
    /// <param name="rawContent">Raw content from model response.</param>
    /// <param name="taskId">Task ID for the job.</param>
    /// <param name="pageIndex">Page index (0-based).</param>
    /// <param name="modelName">Model name for filename.</param>
    /// <param name="tokenCount">Number of tokens in response.</param>
    /// <param name="timestamp">Timestamp of the response.</param>
    /// <returns>Full path to the saved file, or null if not in Development.</returns>
    public static async Task<string?> SaveRawModelResponseAsync(
        string rawContent,
        string taskId,
        int pageIndex,
        string modelName,
        int tokenCount,
        DateTime timestamp)
    {
        // Only save in Development environment
#if DEBUG
        var debugPath = GetDebugPath();
        var subDir = Path.Combine(debugPath, "raw_model_responses", taskId);
        
        if (!Directory.Exists(subDir))
        {
            Directory.CreateDirectory(subDir);
        }
        
        // Filename format: page{pageIndex}_{modelName}_raw.md
        var safeModelName = string.Concat(modelName.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
        var fileName = $"page{pageIndex + 1:D3}_{safeModelName}_raw.md";
        var filePath = Path.Combine(subDir, fileName);
        
        // Build markdown content with token info header
        var markdownContent = $"""
            ---
            TaskId: {taskId}
            PageIndex: {pageIndex + 1}
            Model: {modelName}
            TokenCount: {tokenCount}
            Timestamp: {timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC
            ---
            
            {rawContent}
            """;
        
        await File.WriteAllTextAsync(filePath, markdownContent);
        return filePath;
#else
        await Task.CompletedTask;
        return null;
#endif
    }

    /// <summary>
    /// Saves a Base64 image to the debug directory organized by taskId.
    /// </summary>
    /// <param name="base64">Base64-encoded image data.</param>
    /// <param name="taskId">Task ID to organize files into subfolder.</param>
    /// <param name="fileName">Filename (will be saved in taskId subfolder).</param>
    /// <returns>Full path to the saved file.</returns>
    public static async Task<string> SaveDebugImageAsync(string base64, string taskId, string fileName)
    {
        var debugPath = GetDebugPath();
        var subDir = Path.Combine(debugPath, "cropped_images", taskId);

        if (!Directory.Exists(subDir))
        {
            Directory.CreateDirectory(subDir);
        }

        var filePath = Path.Combine(subDir, fileName);
        await File.WriteAllBytesAsync(filePath, Convert.FromBase64String(base64));
        return filePath;
    }

    /// <summary>
    /// Saves JSON content to the debug directory.
    /// </summary>
    /// <param name="content">JSON content.</param>
    /// <param name="fileName">Filename (will be saved in debug dir).</param>
    /// <returns>Full path to the saved file.</returns>
    public static async Task<string> SaveDebugJsonAsync(string content, string fileName)
    {
        var debugPath = GetDebugPath();
        var filePath = Path.Combine(debugPath, fileName);
        
        await File.WriteAllTextAsync(filePath, content);
        return filePath;
    }

    /// <summary>
    /// Deletes a file if it exists, suppressing exceptions.
    /// </summary>
    /// <param name="filePath">Path to the file to delete.</param>
    public static void DeleteFileSafe(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Suppress exceptions for cleanup operations
        }
    }

    /// <summary>
    /// Deletes temporary upload files matching the taskId and removes the mapping.
    /// Should be called after OCR job completes, fails, or is cancelled.
    /// </summary>
    /// <param name="taskId">Task ID to find and delete files.</param>
    public static async Task DeleteTempUploadFileAsync(string taskId)
    {
        // Find and delete files matching taskId pattern: {taskId}_*
        var uploadPath = GetTempUploadPath();
        var matchingFiles = Directory.GetFiles(uploadPath, $"{taskId}_*");
        
        foreach (var file in matchingFiles)
        {
            DeleteFileSafe(file);
        }

        // Remove mapping (Now async, must be awaited)
        await DeleteJobMapping(taskId);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up old files in a directory based on age.
    /// </summary>
    /// <param name="directoryPath">Directory to clean.</param>
    /// <param name="maxAgeHours">Maximum age in hours before files are deleted.</param>
    public static void CleanupOldFiles(string directoryPath, int maxAgeHours = 24)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            var files = Directory.GetFiles(directoryPath);
            var threshold = DateTime.Now.AddHours(-maxAgeHours);

            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime < threshold)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Suppress exceptions for individual file cleanup
                }
            }
        }
        catch
        {
            // Suppress exceptions for directory cleanup
        }
    }

    /// <summary>
    /// Creates and returns the temporary directory for storing rendered PDF pages during OCR job.
    /// Path pattern: tmp/ocr_images/{taskId}
    /// </summary>
    /// <param name="taskId">Task ID for the job.</param>
    /// <returns>Full path to the job's temporary image directory.</returns>
    public static string CreateJobTempDir(string taskId)
    {
        var path = Path.Combine(_basePath, "tmp", "ocr_images", taskId);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }

    /// <summary>
    /// Cleans up all temporary files for a job by deleting the job's temp directory.
    /// </summary>
    /// <param name="taskId">Task ID to clean up.</param>
    public static void CleanupJobTempDir(string taskId)
    {
        try
        {
            var jobTempPath = CreateJobTempDir(taskId);
            if (Directory.Exists(jobTempPath))
            {
                Directory.Delete(jobTempPath, recursive: true);
            }
        }
        catch
        {
            // Suppress exceptions for cleanup
        }
    }

    /// <summary>
    /// Cleans up all temporary directories on application startup.
    /// Xóa file upload cũ (> 1 giờ) và thư mục OCR image cũ.
    /// </summary>
    /// <param name="maxAgeHours">Tuổi tối đa của file trước khi bị xóa (mặc định: 1 giờ).</param>
    public static void CleanupAllStartupTempFiles(int maxAgeHours = 1)
    {
        try
        {
            // 1. Xóa toàn bộ file PDF upload chưa được xử lý (> 1 giờ)
            CleanupOldFiles(GetTempUploadPath(), maxAgeHours);

            // 2. Xóa toàn bộ thư mục con trong tmp/ocr_images (ảnh render từng trang)
            var ocrImagesDir = Path.Combine(_basePath, "tmp", "ocr_images");
            if (Directory.Exists(ocrImagesDir))
            {
                foreach (var jobDir in Directory.GetDirectories(ocrImagesDir))
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(jobDir);
                        // Chỉ xóa folder cũ hơn maxAgeHours để tránh xóa job đang chạy
                        if (dirInfo.LastWriteTime < DateTime.Now.AddHours(-maxAgeHours))
                        {
                            Directory.Delete(jobDir, recursive: true);
                        }
                    }
                    catch
                    {
                        // Suppress exceptions for individual directory cleanup
                    }
                }
            }
        }
        catch
        {
            // Suppress exceptions for startup cleanup
        }
    }

    /// <summary>
    /// Gets the test output directory for PDF test controller.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    /// <returns>Full path to the test output directory.</returns>
    public static string GetPdfTestPath()
    {
        var path = Path.Combine(_basePath, "tmp_pdf_test");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }

    public static string GetOutputDir()
    {
        return Path.Combine(_basePath, "Outputs");
    }

    /// <summary>
    /// Tìm file JSON (.json) trong thư mục Outputs dựa theo taskId.
    /// Tên file có định dạng: {FileName}_{TaskId}.json
    /// </summary>
    /// <param name="taskId">Mã Task ID cần tìm.</param>
    /// <returns>Đường dẫn tuyệt đối tới file nếu tìm thấy, ngược lại trả về null.</returns>
    public static string? GetJsonResultFilePath(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        if (!Directory.Exists(GetOutputDir()))
        {
            return null;
        }

        // Tìm kiếm file JSON có chứa taskId trong tên
        // Pattern: *_{taskId}.json
        string searchPattern = $"*_{taskId}.json";

        var files = Directory.GetFiles(GetOutputDir(), searchPattern);

        if (files.Length == 0)
        {
            return null;
        }

        // Nếu có nhiều file (do chạy lại nhiều lần?), lấy file mới nhất theo thời gian tạo
        var latestFile = files
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();

        return latestFile?.FullName;
    }

    /// <summary>
    /// <para>Lưu mapping giữa taskId và JobId vào file JSON duy nhất.</para>
    /// <para>JobId là id từ hangfire, taskId là id được gen từ server và được sử dụng trong cấu hình worker redis, .....</para>
    /// </summary>
    public static async Task SaveJobMapping(string taskId, string jobId)
    {
        await _mappingSemaphore.WaitAsync();
        try
        {
            var mappingPath = Path.Combine(_basePath, JOB_MAPPING_FILE);
            var directory = Path.GetDirectoryName(mappingPath);
            
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Đọc danh sách hiện tại
            var mappings = await LoadAllMappingsAsync();

            // Thêm mapping mới
            mappings.Add(new JobMappingItem
            {
                TaskId = taskId,
                JobId = jobId,
                CreatedAt = DateTime.UtcNow
            });

            // Ghi lại file
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var json = JsonSerializer.Serialize(mappings, options);
            await File.WriteAllTextAsync(mappingPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save job mapping: {ex.Message}");
        }
        finally
        {
            _mappingSemaphore.Release();
        }
    }

    /// <summary>
    /// Lấy JobId từ taskId
    /// </summary>
    public static async Task<string?> GetJobIdByTaskId(string taskId)
    {
        await _mappingSemaphore.WaitAsync();
        try
        {
            var mappings = await LoadAllMappingsAsync();
            var mapping = mappings.FirstOrDefault(m => m.TaskId == taskId);
            return mapping?.JobId;
        }
        catch
        {
            return null;
        }
        finally
        {
            _mappingSemaphore.Release();
        }
    }

    /// <summary>
    /// Xóa mapping theo taskId
    /// </summary>
    public static async Task DeleteJobMapping(string taskId)
    {
        await _mappingSemaphore.WaitAsync();
        try
        {
            var mappings = await LoadAllMappingsAsync();
            mappings.RemoveAll(m => m.TaskId == taskId);

            var mappingPath = Path.Combine(_basePath, JOB_MAPPING_FILE);
            
            if (mappings.Count == 0)
            {
                if (File.Exists(mappingPath))
                {
                    File.Delete(mappingPath);
                }
            }
            else
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var json = JsonSerializer.Serialize(mappings, options);
                await File.WriteAllTextAsync(mappingPath, json);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to delete job mapping: {ex.Message}");
        }
        finally
        {
            _mappingSemaphore.Release();
        }
    }

    /// <summary>
    /// Đọc tất cả mappings từ file
    /// </summary>
    private static async Task<List<JobMappingItem>> LoadAllMappingsAsync()
    {
        try
        {
            var mappingPath = Path.Combine(_basePath, JOB_MAPPING_FILE);
            
            if (!File.Exists(mappingPath))
            {
                return new List<JobMappingItem>();
            }

            var json = await File.ReadAllTextAsync(mappingPath);
            var mappings = JsonSerializer.Deserialize<List<JobMappingItem>>(json);
            return mappings ?? new List<JobMappingItem>();
        }
        catch
        {
            return new List<JobMappingItem>();
        }
    }

    /// <summary>
    /// Model đại diện cho 1 mapping item
    /// </summary>
    public class JobMappingItem
    {
        [JsonPropertyName("taskId")]
        public string TaskId { get; set; } = "";

        [JsonPropertyName("jobId")]
        public string JobId { get; set; } = "";

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }
    }
}
