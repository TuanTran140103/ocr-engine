namespace OCREngine.Options;

public class HangfireOption
{
    public required string RedisConnection { get; set; }
    public string DashboardPath { get; set; } = "/hangfire";
    public string DashboardTitle { get; set; } = "Background Jobs";
    public int WorkerCount { get; set; } = 2;
}
