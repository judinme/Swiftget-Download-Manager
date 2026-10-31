using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SwiftGet.Core.Models;

namespace SwiftGet.App.Controls;

public sealed partial class PieceMapControl : UserControl
{
    public static readonly DependencyProperty PieceCellsProperty =
        DependencyProperty.Register(
            nameof(PieceCells),
            typeof(PieceState[]),
            typeof(PieceMapControl),
            new PropertyMetadata(Array.Empty<PieceState>(), OnPieceCellsChanged));

    public PieceState[] PieceCells
    {
        get => (PieceState[])GetValue(PieceCellsProperty);
        set => SetValue(PieceCellsProperty, value);
    }

    private WriteableBitmap? _bitmap;
    private int _cols = 1;
    private int _rows = 1;
    private int _cellWidth = 8;
    private int _cellHeight = 8;
    private const int CellSpacing = 1;

    // BGRA colors
    private static readonly uint ColorMissing = 0xFF262626;      // Dark gray
    private static readonly uint ColorDownloading = 0xFFFF901E;  // Bright blue (BGRA: BB=FF, GG=90, RR=1E)
    private static readonly uint ColorDownloaded = 0xFF71CB2E;   // Green (BGRA: BB=71, GG=CB, RR=2E)
    private static readonly uint ColorVerifying = 0xFF0FC4F1;    // Yellow (BGRA: BB=0F, GG=C4, RR=F1)
    private static readonly uint ColorBackground = 0xFF141414;

    public PieceMapControl()
    {
        InitializeComponent();
        SizeChanged += (s, e) => Redraw();
    }

    private static void OnPieceCellsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PieceMapControl control)
        {
            control.Redraw();
        }
    }

    public void Redraw()
    {
        var cells = PieceCells;
        if (cells == null || cells.Length == 0)
        {
            GridImage.Source = null;
            return;
        }

        int width = (int)ActualWidth;
        int height = (int)ActualHeight;
        if (width <= 10 || height <= 10) return;

        // Compute optimal grid layout
        int totalCells = cells.Length;
        int cellSize = 8;
        if (totalCells > 500) cellSize = 6;
        if (totalCells > 1000) cellSize = 5;

        _cellWidth = cellSize;
        _cellHeight = cellSize;

        int cols = Math.Max(1, width / (cellSize + CellSpacing));
        int rows = (totalCells + cols - 1) / cols;

        int bmpWidth = width;
        int bmpHeight = Math.Max(height, rows * (cellSize + CellSpacing));

        if (_bitmap == null || _bitmap.PixelWidth != bmpWidth || _bitmap.PixelHeight != bmpHeight)
        {
            _bitmap = new WriteableBitmap(bmpWidth, bmpHeight);
            GridImage.Source = _bitmap;
        }

        _cols = cols;
        _rows = rows;

        uint[] pixelBuffer = new uint[bmpWidth * bmpHeight];
        Array.Fill(pixelBuffer, ColorBackground);

        for (int i = 0; i < totalCells; i++)
        {
            int col = i % cols;
            int row = i / cols;

            int startX = col * (cellSize + CellSpacing);
            int startY = row * (cellSize + CellSpacing);

            if (startY + cellSize > bmpHeight) break;

            uint color = cells[i] switch
            {
                PieceState.Downloading => ColorDownloading,
                PieceState.Downloaded => ColorDownloaded,
                PieceState.Verifying => ColorVerifying,
                _ => ColorMissing
            };

            for (int dy = 0; dy < cellSize; dy++)
            {
                int py = startY + dy;
                int rowOffset = py * bmpWidth;
                for (int dx = 0; dx < cellSize; dx++)
                {
                    int px = startX + dx;
                    if (px < bmpWidth)
                    {
                        pixelBuffer[rowOffset + px] = color;
                    }
                }
            }
        }

        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            var byteBuffer = new byte[pixelBuffer.Length * 4];
            Buffer.BlockCopy(pixelBuffer, 0, byteBuffer, 0, byteBuffer.Length);
            stream.Write(byteBuffer, 0, byteBuffer.Length);
        }

        _bitmap.Invalidate();
    }

    private void GridImage_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var cells = PieceCells;
        if (cells == null || cells.Length == 0 || _cols <= 0 || _cellWidth <= 0) return;

        var pt = e.GetCurrentPoint(GridImage).Position;
        int col = (int)(pt.X / (_cellWidth + CellSpacing));
        int row = (int)(pt.Y / (_cellHeight + CellSpacing));

        int index = row * _cols + col;
        if (index >= 0 && index < cells.Length)
        {
            var state = cells[index];
            ToolTipService.SetToolTip(GridImage, $"Cell #{index + 1}: {state}");
        }
        else
        {
            ToolTipService.SetToolTip(GridImage, null);
        }
    }

    private void GridImage_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ToolTipService.SetToolTip(GridImage, null);
    }
}
