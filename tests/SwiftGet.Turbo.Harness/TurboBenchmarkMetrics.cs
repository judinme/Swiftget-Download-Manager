using System;

namespace SwiftGet.Turbo.Harness;

/// <summary>
/// Telemetry metrics captured during download execution.
/// </summary>
public sealed class TurboBenchmarkMetrics
{
    public string ScenarioName { get; init; } = string.Empty;
    public string EngineVariant { get; init; } = string.Empty;
    public long TotalBytesDownloaded { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public double TimeToFirstByteMs { get; init; }
    public double RampUpTimeTo90PercentSec { get; init; }
    public double SteadyStateThroughputMBps { get; init; }
    public double TargetCapacityMBps { get; init; }
    public double SteadyStateUtilizationPercent => TargetCapacityMBps > 0
        ? Math.Min(100.0, Math.Round((SteadyStateThroughputMBps / TargetCapacityMBps) * 100.0, 1))
        : 100.0;
    public double TailTimeSeconds { get; init; } // Duration of final 5% (95% to 100%)
    public double TailTimePercent => TotalDuration.TotalSeconds > 0
        ? Math.Round((TailTimeSeconds / TotalDuration.TotalSeconds) * 100.0, 1)
        : 0.0;
    public int PeakConnections { get; init; }
    public double CpuPercent { get; init; }
    public double AllocatedMemoryMB { get; init; }
    public bool IntegrityVerified { get; init; }
    public string Sha256Hash { get; init; } = string.Empty;

    public override string ToString()
    {
        return $"[{EngineVariant} | {ScenarioName}] " +
               $"Duration: {TotalDuration.TotalSeconds:F2}s, " +
               $"Throughput: {SteadyStateThroughputMBps:F2} MB/s ({SteadyStateUtilizationPercent:F1}% util), " +
               $"TTFB: {TimeToFirstByteMs:F1}ms, " +
               $"Ramp-up (90%): {RampUpTimeTo90PercentSec:F2}s, " +
               $"Tail Time: {TailTimeSeconds:F2}s ({TailTimePercent:F1}%), " +
               $"Integrity: {(IntegrityVerified ? "VALID" : "MISMATCH")}";
    }
}
