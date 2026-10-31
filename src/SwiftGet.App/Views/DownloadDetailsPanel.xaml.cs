using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App.Views;

public sealed partial class DownloadDetailsPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(DownloadDetailsViewModel),
            typeof(DownloadDetailsPanel),
            new PropertyMetadata(null));

    public DownloadDetailsViewModel ViewModel
    {
        get => (DownloadDetailsViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private bool _isResizing;
    private double _initialPointerY;
    private double _initialHeight;

    public DownloadDetailsPanel()
    {
        InitializeComponent();
    }

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isResizing = true;
        SplitterBorder.CapturePointer(e.Pointer);
        try
        {
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth);
        }
        catch { }
        var pt = e.GetCurrentPoint(this).Position;
        _initialPointerY = pt.Y;
        _initialHeight = ViewModel?.PanelHeight ?? ActualHeight;
        e.Handled = true;
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing || ViewModel == null) return;

        var pt = e.GetCurrentPoint(this).Position;
        double deltaY = pt.Y - _initialPointerY;
        // Dragging UP increases height; dragging DOWN decreases height
        double newHeight = Math.Clamp(_initialHeight - deltaY, 140, 420);
        ViewModel.PanelHeight = newHeight;
        Height = newHeight;
        e.Handled = true;
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isResizing)
        {
            _isResizing = false;
            SplitterBorder.ReleasePointerCapture(e.Pointer);
            try
            {
                ProtectedCursor = null;
            }
            catch { }
            e.Handled = true;
        }
    }

    private void CopyIp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConnectionItemViewModel item })
        {
            ViewModel?.CopyConnectionIp(item);
        }
    }

    private void CopyIpPort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConnectionItemViewModel item })
        {
            ViewModel?.CopyConnectionIpPort(item);
        }
    }
}
