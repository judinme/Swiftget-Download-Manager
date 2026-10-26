using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SwiftGet.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace SwiftGet.App.Views;

public sealed partial class DropBoxWindow : Window
{
    public DropBoxViewModel ViewModel { get; }

    public DropBoxWindow(DropBoxViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        AppWindow.Resize(new SizeInt32(140, 140));
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Download with SwiftGet";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
        ViewModel.IsDraggingOver = true;
    }

    private void RootGrid_DragLeave(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOver = false;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOver = false;
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.Text))
            {
                var text = await e.DataView.GetTextAsync();
                await ViewModel.HandleDroppedTextAsync(text).ConfigureAwait(true);
            }
            else if (e.DataView.Contains(StandardDataFormats.Uri))
            {
                var uri = await e.DataView.GetUriAsync();
                await ViewModel.HandleDroppedTextAsync(uri.AbsoluteUri).ConfigureAwait(true);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
