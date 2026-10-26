using System;
using Microsoft.UI.Xaml.Controls;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App.Views;

public sealed partial class OptionsDialog : ContentDialog
{
    public OptionsViewModel ViewModel { get; }

    public OptionsDialog(OptionsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        PrimaryButtonClick += async (s, e) =>
        {
            await ViewModel.SaveAllOptionsAsync().ConfigureAwait(true);
        };
    }
}
