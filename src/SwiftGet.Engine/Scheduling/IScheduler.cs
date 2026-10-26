namespace SwiftGet.Engine.Scheduling;

public interface IScheduler : IDisposable
{
    void Start();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Start and Stop are standard lifecycle methods across download manager schedulers.")]
    void Stop();
    bool IsRunning { get; }
    Task EvaluateSchedulesAsync(DateTime currentLocalTime, CancellationToken ct = default);
}
