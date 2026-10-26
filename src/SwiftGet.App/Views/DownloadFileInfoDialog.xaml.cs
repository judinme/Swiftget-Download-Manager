using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SwiftGet.App.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SwiftGet.App.Views;

public sealed partial class DownloadFileInfoDialog : ContentDialog
{
    public AddUrlViewModel ViewModel { get; }

    public DownloadFileInfoDialog(AddUrlViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(App.CurrentWindow);
            string? selected = Services.FolderPickerHelper.PickFolder(hwnd, ViewModel.SaveDirectory);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                ViewModel.SaveDirectory = selected;
                Bindings.Update();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to pick folder in DownloadFileInfoDialog");
        }
    }
}
