namespace SwiftGet.Core.Models;

public class DownloadQueue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Main Queue";
    public int MaxConcurrentDownloads { get; set; } = 4;
    public long SpeedLimitBytesPerSec { get; set; }
    public bool IsActive { get; set; } = true;
    public int ScheduleDaysMask { get; set; } = 127; // Mon(1) to Sun(64)
    public TimeSpan? StartTimeUtc { get; set; }
    public TimeSpan? StopTimeUtc { get; set; }
    public AfterCompletionAction AfterCompletionAction { get; set; } = AfterCompletionAction.None;
    public string? CustomActionCommand { get; set; }
}
