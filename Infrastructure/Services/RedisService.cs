using System.Collections.Concurrent;
using StackExchange.Redis;
using OCREngine.Applications.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace OCREngine.Infrastructure.Services;

public class RedisService : IRedisService
{
    private readonly IDatabase _db;
    private readonly ILogger<RedisService> _logger;
    private static readonly ConcurrentDictionary<string, string> _scriptCache = new();
    private readonly string _scriptPath;

    public RedisService(IConnectionMultiplexer redis, ILogger<RedisService> logger)
    {
        _db = redis.GetDatabase();
        _logger = logger;
        // Đường dẫn tương đối đến thư mục chứa script
        _scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Infrastructure", "LuaScripts");

        // Nếu không chạy trong unit test, có thể dùng đường dẫn trực tiếp từ project
        if (!Directory.Exists(_scriptPath))
        {
            _scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "Infrastructure", "LuaScripts");
        }
    }

    private async Task<string> GetScriptAsync(string fileName)
    {
        if (_scriptCache.TryGetValue(fileName, out var cachedScript))
            return cachedScript;

        var fullPath = Path.Combine(_scriptPath, fileName);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Lua script not found at {fullPath}");

        var script = await File.ReadAllTextAsync(fullPath);

        // GetOrAdd is atomic: if two threads race here, one wins — both results are valid
        return _scriptCache.GetOrAdd(fileName, script);
    }

    private string GetModelKey(string modelId) => $"ocr:model:{modelId}:workers";

    public async Task<string?> AllocateSlotsAsync(string modelId, string workerId, int totalMaxConcurrency, string? workerDataJson = null)
    {
        try
        {
            var script = await GetScriptAsync("allocate_slots.lua");
            var result = await _db.ScriptEvaluateAsync(script,
                new RedisKey[] { GetModelKey(modelId) },
                new RedisValue[] { workerId, totalMaxConcurrency, workerDataJson ?? "" });

            return result.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing allocate_slots.lua for model {ModelId}, worker {WorkerId}", modelId, workerId);
            return null;
        }
    }

    public async Task<string?> IncrementUsedAsync(string modelId, string workerId)
    {
        try
        {
            var script = await GetScriptAsync("increment_used.lua");
            var result = await _db.ScriptEvaluateAsync(script,
                new RedisKey[] { GetModelKey(modelId) },
                new RedisValue[] { workerId });

            if (result.IsNull)
            {
                // Ném exception để ngắt vòng lặp ở OcrBackgroundJob thay vì log lặp đi lặp lại
                throw new Exception("Worker not found in Redis (canceled or expired)");
            }

            return result.ToString();
        }
        catch (RedisServerException ex) when (ex.Message.Contains("No slots available"))
        {
            return null;
        }
        catch (Exception ex) when (ex.Message.Contains("Worker not found"))
        {
            // Rethrow để Job xử lý việc Cancel
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing increment_used.lua for model {ModelId}, worker {WorkerId}", modelId, workerId);
            return null;
        }
    }

    public async Task<string?> DecrementUsedAsync(string modelId, string workerId)
    {
        try
        {
            var script = await GetScriptAsync("decrement_used.lua");
            var result = await _db.ScriptEvaluateAsync(script,
                new RedisKey[] { GetModelKey(modelId) },
                new RedisValue[] { workerId });

            return result.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing decrement_used.lua for model {ModelId}, worker {WorkerId}", modelId, workerId);
            return null;
        }
    }

    public async Task<string?> RemoveWorkerAsync(string modelId, string workerId)
    {
        try
        {
            var script = await GetScriptAsync("remove_worker.lua");
            var result = await _db.ScriptEvaluateAsync(script,
                new RedisKey[] { GetModelKey(modelId) },
                new RedisValue[] { workerId });

            return result.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing remove_worker.lua for model {ModelId}, worker {WorkerId}", modelId, workerId);
            return null;
        }
    }

    public async Task<bool> CanIncrementAsync(string modelId, string workerId)
    {
        try
        {
            var dataRaw = await _db.HashGetAsync(GetModelKey(modelId), workerId);
            if (dataRaw.IsNull) return false;

            using var doc = JsonDocument.Parse(dataRaw.ToString());
            var root = doc.RootElement;

            int used = root.GetProperty("used").GetInt32();
            int allowSlot = root.GetProperty("allowSlot").GetInt32();

            return used < allowSlot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking capacity for model {ModelId}, worker {WorkerId}", modelId, workerId);
            return false;
        }
    }

    public async Task ClearAllWorkersAsync(string modelId)
    {
        var key = GetModelKey(modelId);
        await _db.KeyDeleteAsync(key);
        _logger.LogInformation("Cleared all workers for model {ModelId} from Redis key {Key}", modelId, key);
    }

    public async Task<bool> RemoveWorkerFromAllModelsAsync(string taskId)
    {
        bool anyRemoved = false;
        try
        {
            // Scan all keys matching the worker pattern
            var endpoints = _db.Multiplexer.GetEndPoints();
            foreach (var endpoint in endpoints)
            {
                var server = _db.Multiplexer.GetServer(endpoint);

                // Pattern matches any model
                await foreach (var key in server.KeysAsync(pattern: "ocr:model:*:workers"))
                {
                    // Try to delete the field (taskId) from the hash
                    bool removed = await _db.HashDeleteAsync(key, taskId);
                    if (removed)
                    {
                        _logger.LogInformation("Removed worker {TaskId} from key {Key}", taskId, key);
                        anyRemoved = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning/removing worker {TaskId} from all models", taskId);
        }
        return anyRemoved;
    }

    public async Task PublishEventAsync(string streamKey, string jsonData)
    {
        try
        {
            await _db.StreamAddAsync(streamKey, "data", jsonData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing event to Redis stream {StreamKey}", streamKey);
        }
    }
    public async Task ClearAllStreamsAsync()
    {
        try
        {
            var endpoints = _db.Multiplexer.GetEndPoints();
            foreach (var endpoint in endpoints)
            {
                var server = _db.Multiplexer.GetServer(endpoint);
                await foreach (var key in server.KeysAsync(pattern: "ocr:stream:*"))
                {
                    await _db.KeyDeleteAsync(key);
                    _logger.LogInformation("Deleted stream key: {Key}", key);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing recursive stream keys");
        }
    }
}
