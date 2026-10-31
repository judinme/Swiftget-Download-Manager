using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.Engine.Tests.Torrent;

public sealed class TorrentLoggingTests
{
    [Fact]
    public void TorrentLogBuffer_ShouldStoreAndRetrieveRecentEntries()
    {
        var buffer = new TorrentLogBuffer(maxCapacity: 5);

        for (int i = 0; i < 10; i++)
        {
            buffer.Add(new TorrentLogEntry
            {
                Level = "INF",
                Category = "ENGINE",
                Message = $"Test message {i}",
                JobId = i % 2 == 0 ? "jobA" : "jobB"
            });
        }

        var allRecent = buffer.GetRecent(maxCount: 10);
        Assert.Equal(5, allRecent.Count);
        Assert.Equal("Test message 5", allRecent[0].Message);
        Assert.Equal("Test message 9", allRecent[4].Message);

        var jobARecent = buffer.GetRecent(jobId: "jobA", maxCount: 10);
        Assert.Equal(2, jobARecent.Count); // 6, 8
        Assert.All(jobARecent, e => Assert.Equal("jobA", e.JobId));
    }

    [Fact]
    public async Task TorrentLogBuffer_ExportToFileAsync_ShouldWriteValidDiagnosticLog()
    {
        var buffer = new TorrentLogBuffer(100);
        buffer.Add(new TorrentLogEntry
        {
            Level = "INF",
            Category = "TRACKER",
            Message = "Announce to udp://tracker.opentrackr.org:1337/announce succeeded. Peers: 42",
            JobId = "job123"
        });
        buffer.Add(new TorrentLogEntry
        {
            Level = "WRN",
            Category = "PEER",
            Message = "Handshake timed out with 192.168.1.10:6881",
            JobId = "job123"
        });

        var tempFile = Path.Combine(Path.GetTempPath(), $"swiftget_test_log_{Guid.NewGuid():N}.txt");
        try
        {
            await buffer.ExportToFileAsync(tempFile, jobId: "job123", CancellationToken.None);

            Assert.True(File.Exists(tempFile));
            var text = await File.ReadAllTextAsync(tempFile);
            Assert.Contains("# SwiftGet Torrent Diagnostic Log", text);
            Assert.Contains("udp://tracker.opentrackr.org:1337/announce", text);
            Assert.Contains("192.168.1.10:6881", text);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void MonoTorrentEngine_LogTorrent_ShouldInvokeLogReceivedAndPopulateBuffer()
    {
        var engine = new MonoTorrentEngine();
        TorrentLogEntry? receivedEntry = null;

        engine.LogReceived += (s, e) =>
        {
            receivedEntry = e.Entry;
        };

        engine.LogTorrent("INF", "TRACKER", "Announce sent to tracker", "job456");

        Assert.NotNull(receivedEntry);
        Assert.Equal("INF", receivedEntry!.Level);
        Assert.Equal("TRACKER", receivedEntry.Category);
        Assert.Equal("Announce sent to tracker", receivedEntry.Message);
        Assert.Equal("job456", receivedEntry.JobId);

        var recent = engine.GetRecentLogs("job456");
        Assert.NotEmpty(recent);
        Assert.Equal("Announce sent to tracker", recent[0].Message);
    }
}
