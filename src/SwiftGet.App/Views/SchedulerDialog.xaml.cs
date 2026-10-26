using System;
using Microsoft.UI.Xaml.Controls;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App.Views;

public sealed partial class SchedulerDialog : ContentDialog
{
    public SchedulerViewModel ViewModel { get; }

    public SchedulerDialog(SchedulerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        PrimaryButtonClick += async (s, e) =>
        {
            await ViewModel.SaveSettingsAsync().ConfigureAwait(true);
        };

        SecondaryButtonClick += async (s, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                await ViewModel.SaveSettingsAsync().ConfigureAwait(true);
                await ViewModel.StartQueueNowAsync().ConfigureAwait(true);
            }
            finally
            {
                deferral.Complete();
            }
        };
    }
}
