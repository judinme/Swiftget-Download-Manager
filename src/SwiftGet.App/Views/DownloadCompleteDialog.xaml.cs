using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace SwiftGet.App.Views;

public sealed partial class DownloadCompleteDialog : ContentDialog
{
    public string DownloadUrl { get; }
    public string SavePath { get; }
    public long FileSizeBytes { get; }

    public DownloadCompleteDialog(string url, string savePath, long fileSizeBytes)
    {
        DownloadUrl = url ?? string.Empty;
        SavePath = SwiftGet.Core.IO.PathResolver.GetPhysicalPath(savePath ?? string.Empty);
        FileSizeBytes = fileSizeBytes;

        InitializeComponent();

        UrlTextBox.Text = DownloadUrl;
        SavePathTextBox.Text = SavePath;

        SubtitleTextBlock.Text = $"Downloaded {SwiftGet.Engine.IO.FileNameSanitizer.FormatFileSizeWithExactBytes(fileSizeBytes)}";

        var ext = Path.GetExtension(SavePath);
        if (SwiftGet.Core.IO.FileNameResolver.IsDangerous(ext))
        {
            DangerousWarningBar.IsOpen = true;
        }

        Loaded += (s, e) =>
        {
            try
            {
                UrlTextBox.Select(0, 0);
                SavePathTextBox.Select(0, 0);
            }
            catch { }
        };
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(SavePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = SavePath,
                    UseShellExecute = true
                });
            }
        }
        catch { }
        Hide();
    }

    private void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(SavePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = $"shell32.dll,OpenAs_RunDLL \"{SavePath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(SavePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{SavePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(SavePath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
        }
        catch { }
    }

    private void DragBadge_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(SavePath) && File.Exists(SavePath))
            {
                args.Data.SetText(SavePath);
                args.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Link;
            }
        }
        catch { }
    }
}
