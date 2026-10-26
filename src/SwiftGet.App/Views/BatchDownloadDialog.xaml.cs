using System;
using Microsoft.UI.Xaml.Controls;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App.Views;

public sealed partial class BatchDownloadDialog : ContentDialog
{
    public BatchDownloadViewModel ViewModel { get; }

    public BatchDownloadDialog(BatchDownloadViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        PrimaryButtonClick += async (s, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                await ViewModel.AddBatchDownloadsAsync().ConfigureAwait(true);
            }
            finally
            {
                deferral.Complete();
            }
        };

        SecondaryButtonClick += async (s, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                await ViewModel.AddBatchDownloadsAsync().ConfigureAwait(true);
            }
            finally
            {
                deferral.Complete();
            }
        };
    }
}
