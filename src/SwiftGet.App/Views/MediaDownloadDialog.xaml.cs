using System;
using Microsoft.UI.Xaml.Controls;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App.Views;

public sealed partial class MediaDownloadDialog : ContentDialog
{
    public MediaDownloadViewModel ViewModel { get; }

    public MediaDownloadDialog(MediaDownloadViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        IsPrimaryButtonEnabled = ViewModel.CanDownload;
        IsSecondaryButtonEnabled = ViewModel.CanDownload;
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.CanDownload))
            {
                IsPrimaryButtonEnabled = ViewModel.CanDownload;
                IsSecondaryButtonEnabled = ViewModel.CanDownload;
            }
        };

        PrimaryButtonClick += async (s, e) =>
        {
            if (ViewModel.IsDrmProtected || ViewModel.HasError || string.IsNullOrWhiteSpace(ViewModel.MediaUrl))
            {
                e.Cancel = true;
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                await ViewModel.EnqueueDownloadAsync().ConfigureAwait(true);
            }
            finally
            {
                deferral.Complete();
            }
        };

        SecondaryButtonClick += async (s, e) =>
        {
            if (ViewModel.IsDrmProtected || ViewModel.HasError || string.IsNullOrWhiteSpace(ViewModel.MediaUrl))
            {
                e.Cancel = true;
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                await ViewModel.EnqueueDownloadAsync().ConfigureAwait(true);
            }
            finally
            {
                deferral.Complete();
            }
        };
    }
}
