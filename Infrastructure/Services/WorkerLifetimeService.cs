using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OCREngine.Applications.Interfaces;
using OCREngine.Models.Enum;

namespace OCREngine.Infrastructure.Services;

public class WorkerLifetimeService : IHostedService
{
    private readonly IRedisService _redisService;
    private readonly ILogger<WorkerLifetimeService> _logger;

    public WorkerLifetimeService(IRedisService redisService, ILogger<WorkerLifetimeService> logger)
    {
        _redisService = redisService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WorkerLifetimeService is starting. Cleaning up stale worker registrations...");

        // Khi khởi động, chúng ta dọn dẹp sạch HSet để tránh các worker từ phiên cũ (nếu bị kill -9)
        // Lưu ý: Nếu bạn chạy nhiều instance (nhiều server), bạn có thể muốn đổi sang logic xóa chỉ worker của máy này.
        // Ở đây tôi triển khai xóa tất cả theo yêu cầu "hủy hết các worker trong hset".
        await CleanupAllModelsAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WorkerLifetimeService is stopping. Gracefully cleaning up workers...");

        // Khi shutdown bình thường (Ctrl+C, SIGTERM)
        await CleanupAllModelsAsync();
    }

    private async Task CleanupAllModelsAsync()
    {
        try
        {
            // Duyệt qua tất cả các model được hỗ trợ để dọn dẹp Redis
            foreach (LlmSupport model in Enum.GetValues(typeof(LlmSupport)))
            {
                string modelId = model.ToString();
                await _redisService.ClearAllWorkersAsync(modelId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during worker cleanup in WorkerLifetimeService");
        }
    }
}
