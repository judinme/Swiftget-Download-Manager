using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SwiftGet.App.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SwiftGet.App.Views;

public sealed partial class CreateTorrentDialog : ContentDialog
{
    public CreateTorrentViewModel ViewModel { get; }

    public CreateTorrentDialog(CreateTorrentViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        PrimaryButtonClick += CreateTorrentDialog_PrimaryButtonClick;
    }

    private async void CreateTorrentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var success = await ViewModel.CreateTorrentFileAsync();
            if (!success)
            {
                args.Cancel = true;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            ViewModel.SourcePath = file.Path;
            if (string.IsNullOrWhiteSpace(ViewModel.SaveTorrentPath) || ViewModel.SaveTorrentPath.EndsWith("CreatedTorrent.torrent", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(file.Path) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                ViewModel.SaveTorrentPath = Path.Combine(dir, $"{file.Name}.torrent");
            }
        }
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            ViewModel.SourcePath = folder.Path;
            if (string.IsNullOrWhiteSpace(ViewModel.SaveTorrentPath) || ViewModel.SaveTorrentPath.EndsWith("CreatedTorrent.torrent", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(folder.Path) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                ViewModel.SaveTorrentPath = Path.Combine(parent, $"{folder.Name}.torrent");
            }
        }
    }

    private async void BrowseSave_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("BitTorrent File", [".torrent"]);
        picker.DefaultFileExtension = ".torrent";
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(ViewModel.SourcePath) + ".torrent";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            ViewModel.SaveTorrentPath = file.Path;
        }
    }
}
