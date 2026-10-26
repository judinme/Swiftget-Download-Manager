using FluentAssertions;
using SwiftGet.Engine.IO;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class SparseFileWriterTests : IAsyncLifetime
{
    private readonly string _testFile = Path.Combine(Path.GetTempPath(), $"sparse_test_{Guid.NewGuid():N}.bin");
    private SparseFileWriter? _writer;

    public Task InitializeAsync()
    {
        _writer = new SparseFileWriter(_testFile);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_writer != null)
        {
            await _writer.DisposeAsync();
        }

        try
        {
            if (File.Exists(_testFile)) File.Delete(_testFile);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task ConcurrentDisjointWrites_ShouldNotCorruptData()
    {
        const int chunkSize = 4096;
        const int chunksCount = 16;
        const long totalSize = chunkSize * chunksCount;

        await _writer!.PreallocateAsync(totalSize);

        // Prepare deterministic data for each chunk
        var expectedData = new byte[totalSize];
        var tasks = new List<Task>();

        for (var i = 0; i < chunksCount; i++)
        {
            var chunkIndex = i;
            var offset = chunkIndex * chunkSize;
            var buffer = new byte[chunkSize];
            Array.Fill(buffer, (byte)(chunkIndex + 1));
            Buffer.BlockCopy(buffer, 0, expectedData, offset, chunkSize);

            tasks.Add(Task.Run(async () =>
            {
                await _writer.WriteAtAsync(offset, buffer);
            }));
        }

        await Task.WhenAll(tasks);
        await _writer.FlushAsync();
        await _writer.CloseAsync();

        // Read back and verify exact byte content
        var actualData = await File.ReadAllBytesAsync(_testFile);
        actualData.Length.Should().Be((int)totalSize);
        actualData.Should().Equal(expectedData);
    }

    [Fact]
    public async Task PreallocateAsync_OnExistingFile_DoesNotTruncate()
    {
        // Setup existing file with 8192 bytes
        await _writer!.PreallocateAsync(8192);
        var initialData = new byte[8192];
        Array.Fill(initialData, (byte)0xAB);
        await _writer.WriteAtAsync(0, initialData);
        await _writer.FlushAsync();
        await _writer.CloseAsync();

        // Re-open existing file with PreallocateAsync requested at smaller size 4096 (simulating resume probe)
        var resumingWriter = new SparseFileWriter(_testFile);
        await resumingWriter.PreallocateAsync(4096);
        resumingWriter.FileLength.Should().Be(8192); // Not truncated!
        await resumingWriter.CloseAsync();

        var diskData = await File.ReadAllBytesAsync(_testFile);
        diskData.Length.Should().Be(8192);
        diskData.Should().Equal(initialData);
    }
}
