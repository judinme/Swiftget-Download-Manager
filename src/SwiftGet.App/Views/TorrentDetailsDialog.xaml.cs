using System;
using Microsoft.UI.Xaml.Controls;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App.Views;

public sealed partial class TorrentDetailsDialog : ContentDialog
{
    public TorrentDetailsViewModel ViewModel { get; }

    public TorrentDetailsDialog(TorrentDetailsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        Closing += (s, e) =>
        {
            ViewModel.StopPolling();
        };

        Closed += (s, e) =>
        {
            ViewModel.Dispose();
        };
    }
}
