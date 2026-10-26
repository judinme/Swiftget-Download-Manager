using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App.Views;

public sealed partial class GrabberWizardDialog : ContentDialog
{
    public GrabberWizardViewModel ViewModel { get; }

    public GrabberWizardDialog(GrabberWizardViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    private async void NextOrFinish_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentStep == 4)
        {
            await ViewModel.AddSelectedDownloadsAsync().ConfigureAwait(true);
            Hide();
        }
        else
        {
            await ViewModel.NextStepAsync().ConfigureAwait(true);
        }
    }
}
