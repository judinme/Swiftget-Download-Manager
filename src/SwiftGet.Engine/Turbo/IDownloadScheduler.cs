using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SwiftGet.Engine.Turbo;

/// <summary>
/// Snapshot of an individual worker connection's state for UI telemetry.
/// </summary>
public sealed record ConnectionSnapshot(
    int Index,
    string Status,
    long StartOffset,
    long EndOffset,
    long DownloadedBytes,
    double SpeedBytesPerSec);

/// <summary>
/// Abstraction for download schedulers, allowing the Turbo Engine to run side-by-side
/// with the legacy segment scheduler behind a feature flag.
/// </summary>
public interface IDownloadScheduler : IDisposable
{
    /// <summary>Total bytes downloaded and committed to disk.</summary>
    long DownloadedBytes { get; }

    /// <summary>Active connection count.</summary>
    int ActiveConnections { get; }

    /// <summary>Live telemetry on path capacity, utilization %, and bottleneck diagnostics.</summary>
    CapacityEstimate CurrentCapacity { get; }

    /// <summary>Optional hook triggered when the first byte of payload is read.</summary>
    Action<HttpResponseMessage, byte[]>? OnFirstChunkRead { get; set; }

    /// <summary>Captures snapshots of all active and pending connections for real-time UI rendering.</summary>
    IReadOnlyList<ConnectionSnapshot> GetConnectionSnapshots();

    /// <summary>Runs the download until completion or cancellation.</summary>
    Task ExecuteAsync(CancellationToken ct);
}
