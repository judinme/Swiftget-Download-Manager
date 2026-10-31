using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SwiftGet.App.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

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

    private void BrowseDefaultSaveDirectory_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = WindowNative.GetWindowHandle(App.CurrentWindow);
        string? selected = Services.FolderPickerHelper.PickFolder(hwnd, ViewModel.TorrentDefaultSaveDirectory);
        if (!string.IsNullOrEmpty(selected))
        {
            ViewModel.TorrentDefaultSaveDirectory = selected;
        }
    }

    private async void BrowseIpFilter_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".dat");
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".p2p");
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            ViewModel.TorrentIpFilterPath = file.Path;
            ViewModel.IpFilterStatus = $"Selected: {file.Name}";
        }
    }
}
