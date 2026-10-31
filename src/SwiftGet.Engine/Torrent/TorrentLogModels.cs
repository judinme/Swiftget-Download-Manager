using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SwiftGet.Engine.Torrent;

public sealed class TorrentLogEntry
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string Level { get; init; } = "INF"; // INF, WRN, ERR, DBG
    public string Category { get; init; } = "Torrent"; // ENGINE, TRACKER, PEER, DHT, METADATA, STORAGE
    public string Message { get; init; } = string.Empty;
    public string? JobId { get; init; }

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "[{0:HH:mm:ss.fff}] [{1}] [{2}] {3}", TimestampUtc, Level, Category, Message);
}

public sealed class TorrentLogEventArgs : EventArgs
{
    public TorrentLogEntry Entry { get; }

    public TorrentLogEventArgs(TorrentLogEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }
}

public sealed class TorrentLogBuffer
{
    private readonly LinkedList<TorrentLogEntry> _entries = new();
    private readonly object _lock = new();
    private readonly int _maxCapacity;

    public TorrentLogBuffer(int maxCapacity = 1000)
    {
        _maxCapacity = maxCapacity > 0 ? maxCapacity : 1000;
    }

    public void Add(TorrentLogEntry entry)
    {
        if (entry == null) return;
        lock (_lock)
        {
            _entries.AddLast(entry);
            if (_entries.Count > _maxCapacity)
            {
                _entries.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<TorrentLogEntry> GetRecent(string? jobId = null, int maxCount = 200)
    {
        lock (_lock)
        {
            IEnumerable<TorrentLogEntry> query = _entries;
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                query = query.Where(e => string.IsNullOrWhiteSpace(e.JobId) || string.Equals(e.JobId, jobId, StringComparison.OrdinalIgnoreCase));
            }
            return query.TakeLast(maxCount).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    public async Task ExportToFileAsync(string destinationPath, string? jobId = null, CancellationToken ct = default)
    {
        var logs = GetRecent(jobId, maxCount: _maxCapacity);
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# SwiftGet Torrent Diagnostic Log - Generated at {DateTime.UtcNow:u}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Total Log Entries: {logs.Count}");
        sb.AppendLine();

        foreach (var entry in logs)
        {
            sb.AppendLine(entry.ToString());
        }

        await File.WriteAllTextAsync(destinationPath, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
    }
}
