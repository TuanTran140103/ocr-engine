/// <summary>
/// Service to manage dynamic worker slot allocation using Redis Lua scripts.
/// 
/// Redis Data Structure:
/// - Key: "ocr:model:{modelId}:workers" (Type: Hash)
/// - Field: "{workerId}" (e.g., "worker-01", "worker-node-A")
/// - Value: JSON string:
/// {
///    "allowSlot": 5,        // Current max concurrency limit for this worker
///    "maxConcurrency": 10,  // (Optional) Hard limit of the worker itself
///    "used": 2,             // Number of slots currently in use
///    "remainingPage": 45,   // Pages left to process (triggers redistribution when &lt; allowSlot)
///    "TotalPage": 100       // Initial total pages
/// }
/// </summary>
public interface IRedisService
{
    Task<string?> AllocateSlotsAsync(string modelId, string workerId, int totalMaxConcurrency, string? workerDataJson = null);
    Task<string?> IncrementUsedAsync(string modelId, string workerId);
    Task<string?> DecrementUsedAsync(string modelId, string workerId);
    Task<string?> RemoveWorkerAsync(string modelId, string workerId);
    Task<bool> CanIncrementAsync(string modelId, string workerId);
    Task ClearAllWorkersAsync(string modelId);
    Task<bool> RemoveWorkerFromAllModelsAsync(string taskId);
    Task PublishEventAsync(string streamKey, string jsonData);
    Task ClearAllStreamsAsync();
}
