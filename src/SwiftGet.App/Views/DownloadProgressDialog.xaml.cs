using System;
using Microsoft.UI.Xaml;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App.Views;

public sealed partial class DownloadProgressDialog : Window
{
    public DownloadDialogViewModel ViewModel { get; }

    public DownloadProgressDialog(DownloadDialogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Title = $"SwiftGet - {ViewModel.Item.FileName}";

        ViewModel.Item.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DownloadItemViewModel.Status) && ViewModel.Item.Status == Core.Models.DownloadStatus.Completed)
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (ViewModel.CloseOnCompletion)
                    {
                        Close();
                    }
                });
            }
        };
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
