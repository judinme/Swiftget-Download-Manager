using System;
using SwiftGet.Core.Models;

namespace SwiftGet.Core.IO;

public static class PieceGridDownsampler
{
    public const int MaxCells = 1500;

    /// <summary>
    /// Downsamples a piece states array into a maximum number of display cells (default 1500),
    /// preserving active downloading and verifying states with priority.
    /// Runs in O(N) time with bounded memory.
    /// </summary>
    public static PieceState[] Downsample(ReadOnlySpan<PieceState> pieces, int maxCells = MaxCells)
    {
        if (pieces.IsEmpty) return Array.Empty<PieceState>();
        if (pieces.Length <= maxCells) return pieces.ToArray();

        int targetCells = Math.Clamp(maxCells, 1, 2000);
        var result = new PieceState[targetCells];
        double ratio = (double)pieces.Length / targetCells;

        for (int i = 0; i < targetCells; i++)
        {
            int start = (int)(i * ratio);
            int end = Math.Min(pieces.Length - 1, (int)((i + 1) * ratio));
            if (end < start) end = start;

            bool hasVerifying = false;
            bool hasDownloading = false;
            bool hasDownloaded = false;
            bool allDownloaded = true;

            for (int p = start; p <= end; p++)
            {
                var s = pieces[p];
                if (s == PieceState.Verifying) hasVerifying = true;
                else if (s == PieceState.Downloading) hasDownloading = true;
                else if (s == PieceState.Downloaded) hasDownloaded = true;
                else allDownloaded = false;
            }

            if (hasVerifying)
            {
                result[i] = PieceState.Verifying;
            }
            else if (hasDownloading)
            {
                result[i] = PieceState.Downloading;
            }
            else if (allDownloaded || hasDownloaded)
            {
                result[i] = PieceState.Downloaded;
            }
            else
            {
                result[i] = PieceState.Missing;
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a piece grid snapshot for segmented HTTP or single-stream downloads.
    /// Maps downloaded byte ranges onto the grid.
    /// </summary>
    public static PieceState[] GenerateForHttp(long downloadedBytes, long totalBytes, int maxCells = 100, bool isActivelyDownloading = true)
    {
        if (totalBytes <= 0)
        {
            // Unknown total bytes (stream/chunked)
            int count = Math.Min(maxCells, 50);
            var cells = new PieceState[count];
            if (downloadedBytes > 0)
            {
                for (int i = 0; i < count - 1; i++) cells[i] = PieceState.Downloaded;
                cells[count - 1] = isActivelyDownloading ? PieceState.Downloading : PieceState.Downloaded;
            }
            return cells;
        }

        int targetCells = Math.Clamp(maxCells, 10, 200);
        var result = new PieceState[targetCells];
        double pct = Math.Clamp((double)downloadedBytes / totalBytes, 0.0, 1.0);
        int completeCells = (int)Math.Floor(pct * targetCells);

        for (int i = 0; i < completeCells; i++)
        {
            result[i] = PieceState.Downloaded;
        }

        if (isActivelyDownloading && completeCells < targetCells)
        {
            result[completeCells] = PieceState.Downloading;
            for (int i = completeCells + 1; i < targetCells; i++)
            {
                result[i] = PieceState.Missing;
            }
        }
        else
        {
            for (int i = completeCells; i < targetCells; i++)
            {
                result[i] = PieceState.Missing;
            }
        }

        return result;
    }
}
