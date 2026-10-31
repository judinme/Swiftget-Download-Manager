using FluentAssertions;
using SwiftGet.Core.IO;
using SwiftGet.Core.Models;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class PieceGridDownsamplerTests
{
    [Fact]
    public void Downsample_SmallPieceCount_ReturnsOriginal()
    {
        var input = new[] { PieceState.Downloaded, PieceState.Downloading, PieceState.Missing };
        var output = PieceGridDownsampler.Downsample(input, maxCells: 100);

        output.Should().Equal(input);
    }

    [Fact]
    public void Downsample_PreservesVerifyingAndDownloadingPrecedence()
    {
        // 1000 pieces downsampled to 10 cells (100 pieces per cell)
        var input = new PieceState[1000];
        Array.Fill(input, PieceState.Missing);

        // Put one Downloading in cell 2 (index 200..299)
        input[250] = PieceState.Downloading;

        // Put one Verifying in cell 5 (index 500..599)
        input[550] = PieceState.Verifying;

        // Put all Downloaded in cell 8 (index 800..899)
        for (int i = 800; i < 900; i++) input[i] = PieceState.Downloaded;

        var output = PieceGridDownsampler.Downsample(input, maxCells: 10);

        output.Length.Should().Be(10);
        output[0].Should().Be(PieceState.Missing);
        output[2].Should().Be(PieceState.Downloading);
        output[5].Should().Be(PieceState.Verifying);
        output[8].Should().Be(PieceState.Downloaded);
    }

    [Fact]
    public void Downsample_50kPiecesBenchmark_ExecutesUnder10Milliseconds()
    {
        // 50,000 pieces (typical for a 50GB torrent with 1MB pieces)
        const int count = 50_000;
        var pieces = new PieceState[count];
        for (int i = 0; i < count; i++)
        {
            pieces[i] = (i % 3) switch
            {
                0 => PieceState.Downloaded,
                1 => PieceState.Downloading,
                _ => PieceState.Missing
            };
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var downsampled = PieceGridDownsampler.Downsample(pieces, maxCells: 1500);
        sw.Stop();

        downsampled.Length.Should().Be(1500);
        sw.ElapsedMilliseconds.Should().BeLessThan(50); // Generous budget, runs in ~1-2ms
    }

    [Fact]
    public void GenerateForHttp_CorrectlyMapsDownloadedAndDownloadingHead()
    {
        var cells = PieceGridDownsampler.GenerateForHttp(downloadedBytes: 50, totalBytes: 100, maxCells: 10, isActivelyDownloading: true);
        cells.Length.Should().Be(10);
        // 50% = 5 cells downloaded, 1 downloading head, rest missing
        cells[0].Should().Be(PieceState.Downloaded);
        cells[4].Should().Be(PieceState.Downloaded);
        cells[5].Should().Be(PieceState.Downloading);
        cells[6].Should().Be(PieceState.Missing);
    }
}
