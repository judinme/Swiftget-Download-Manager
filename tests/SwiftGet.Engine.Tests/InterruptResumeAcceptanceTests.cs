using System.Security.Cryptography;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Coordinator;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class InterruptResumeAcceptanceTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"swiftget_m1_{Guid.NewGuid():N}");
    private TestDownloadServer? _server;
    private SqliteDatabase? _db;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _server = new TestDownloadServer(payloadSize: 256 * 1024); // 256 KB
        _db = new SqliteDatabase(Path.Combine(_tempDir, "test.db"));
        await _db.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server != null) await _server.DisposeAsync();
        if (_db != null) await _db.DisposeAsync();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task OneThousandCycle_InterruptAndResume_ShouldProduceBitExactFileWithoutCorruption()
    {
        const int totalInterruptCycles = 1000;
        var destinationPath = Path.Combine(_tempDir, "downloaded.bin");

        var item = new DownloadItem
        {
            Url = "http://localhost/test_payload.bin",
            DestinationPath = destinationPath,
            FileName = "downloaded.bin",
            ChecksumExpected = _server!.Sha256Hash,
            ChecksumType = ChecksumType.Sha256
        };

        var coordinator = new DownloadCoordinator(_server.Client, _db!);
        var interruptsPerformed = 0;

        for (var cycle = 0; cycle < totalInterruptCycles; cycle++)
        {
            // Set up cancellation token with a rapid, randomized timeout
            using var cts = new CancellationTokenSource();
            var cancelDelayMs = Random.Shared.Next(1, 15);
            cts.CancelAfter(cancelDelayMs);

            try
            {
                await coordinator.StartAsync(
                    item,
                    maxConnections: 4,
                    minSplitSize: 1024 * 16,
                    ct: cts.Token);

                // If it completed, reset destination and database to continue testing interrupts
                if (item.Status == DownloadStatus.Completed)
                {
                    // Verify the completed file matches source hash
                    var completedHash = await DownloadCoordinator.ComputeChecksumAsync(destinationPath, ChecksumType.Sha256, CancellationToken.None);
                    completedHash.Should().Be(_server.Sha256Hash);

                    // Re-arm for remaining cycles
                    if (File.Exists(destinationPath)) File.Delete(destinationPath);
                    item.Id = Guid.NewGuid().ToString("N");
                    item.DownloadedBytes = 0;
                    item.Segments.Clear();
                    item.Status = DownloadStatus.Queued;
                }
            }
            catch (OperationCanceledException)
            {
                interruptsPerformed++;
                item.Status.Should().Be(DownloadStatus.Paused);

                // Reload from SQLite to verify checkpoint persistence after crash/interrupt
                var reloaded = await _db!.GetDownloadAsync(item.Id);
                reloaded.Should().NotBeNull();
                reloaded!.Status.Should().Be(DownloadStatus.Paused);
            }
        }

        // Final completion run: let it finish completely without interruption
        using var finalCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await coordinator.StartAsync(
            item,
            maxConnections: 4,
            minSplitSize: 1024 * 16,
            ct: finalCts.Token);

        item.Status.Should().Be(DownloadStatus.Completed);

        // Verify destination file bit-for-bit
        var finalData = await File.ReadAllBytesAsync(destinationPath);
        finalData.Should().Equal(_server.Payload);

        var finalHash = await DownloadCoordinator.ComputeChecksumAsync(destinationPath, ChecksumType.Sha256, CancellationToken.None);
        finalHash.Should().Be(_server.Sha256Hash);

        interruptsPerformed.Should().BeGreaterThan(20, "A significant number of mid-flight interrupts must have occurred");
    }
}
