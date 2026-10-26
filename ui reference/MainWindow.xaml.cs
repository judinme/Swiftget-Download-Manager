using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;

namespace SwiftGet
{
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<DownloadItem> AllDownloads { get; } = new();
        public ObservableCollection<DownloadItem> FilteredDownloads { get; } = new();

        public MainWindow()
        {
            this.InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            SeedSampleDownloads();
            RefreshFilter();
            BuildChunkVisualizer();
        }

        private void SeedSampleDownloads()
        {
            AllDownloads.Add(new DownloadItem
            {
                FileName = "www.1TamilMV.solar - Jailer 2 (2026) Tamil HQ - 1080p - x264 - AAC - 2.9GB.mkv",
                QueueName = "Q1",
                SizeText = "3.03 GB",
                Status = "Completed",
                Progress = 100,
                SpeedText = "0 B/s",
                EtaText = "—",
                AddedDate = "Oct 24",
                Category = "Video",
                Folder = @"D:\Movies",
                IconGlyph = "\uE714",
                AccentColor = "#4ADE80",
                StatusColor = "#4ADE80"
            });

            AllDownloads.Add(new DownloadItem
            {
                FileName = "PowerShell-7.6.8-win-x64.msi",
                QueueName = "Q1",
                SizeText = "112.05 MB",
                Status = "Downloading",
                Progress = 68,
                SpeedText = "18.4 MB/s",
                EtaText = "00:02",
                AddedDate = "Today",
                Category = "Programs",
                Folder = @"C:\Users\Admin\Downloads\Programs",
                IconGlyph = "\uE756",
                AccentColor = "#38BDF8",
                StatusColor = "#38BDF8"
            });

            AllDownloads.Add(new DownloadItem
            {
                FileName = "Antminer-hash-board-ASIC-repair-guide.zip",
                QueueName = "Q2",
                SizeText = "19.01 MB",
                Status = "Paused",
                Progress = 42,
                SpeedText = "0 B/s",
                EtaText = "Paused",
                AddedDate = "Oct 24",
                Category = "Compressed",
                Folder = @"D:\Archives",
                IconGlyph = "\uF012",
                AccentColor = "#FBBF24",
                StatusColor = "#FBBF24"
            });
        }

        private void RefreshFilter(string search = "", string category = "All")
        {
            FilteredDownloads.Clear();
            var query = AllDownloads.Where(d =>
                (string.IsNullOrWhiteSpace(search) || d.FileName.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
                (category == "All" ||
                 (category == "Completed" && d.Status == "Completed") ||
                 (category == "Unfinished" && d.Status != "Completed") ||
                 d.Category == category));

            foreach (var item in query)
                FilteredDownloads.Add(item);
        }

        private void BuildChunkVisualizer()
        {
            for (int i = 0; i < 32; i++)
            {
                ChunksGrid.Items.Add(new Border
                {
                    Width = 28,
                    Height = 14,
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(3),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        i < 22 ? Windows.UI.Color.FromArgb(255, 56, 189, 248) :
                        i < 26 ? Windows.UI.Color.FromArgb(255, 74, 222, 128) :
                                 Windows.UI.Color.FromArgb(255, 38, 41, 52))
                });
            }
        }

        private async void AddDownload_Click(object sender, RoutedEventArgs e)
        {
            var urlBox = new TextBox
            {
                PlaceholderText = "https://example.com/file.mkv or magnet:?xt=...",
                Width = 420
            };

            var dialog = new ContentDialog
            {
                Title = "Add New Download",
                Content = urlBox,
                PrimaryButtonText = "Download Now",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ElementTheme.Dark,
                XamlRoot = this.Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(urlBox.Text))
            {
                var name = urlBox.Text.Split('/').LastOrDefault() ?? "New_Download.bin";
                AllDownloads.Insert(0, new DownloadItem
                {
                    FileName = name,
                    QueueName = "Q1",
                    SizeText = "450.0 MB",
                    Status = "Downloading",
                    Progress = 12,
                    SpeedText = "12.8 MB/s",
                    EtaText = "00:31",
                    AddedDate = "Just now",
                    Category = "Video",
                    Folder = @"D:\Downloads",
                    IconGlyph = "\uE896",
                    AccentColor = "#38BDF8",
                    StatusColor = "#38BDF8"
                });
                RefreshFilter(SearchBox.Text);
            }
        }

        private void DownloadsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DownloadsListView.SelectedItem is DownloadItem item)
            {
                InspectorTitle.Text = item.FileName;
                InspectorFolder.Text = item.Folder;
                InspectorDownloaded.Text = item.SizeText;
                InspectorAdded.Text = item.AddedDate;
                FooterSelectedTicker.Text = item.FileName;
            }
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
            => RefreshFilter(sender.Text);

        private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CategoryList.SelectedItem is ListViewItem item && item.Tag is string tag)
                RefreshFilter(SearchBox?.Text ?? "", tag);
        }

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
            => MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;

        private void ToggleInspector_Click(object sender, RoutedEventArgs e)
        {
            bool visible = BottomInspectorPanel.Visibility == Visibility.Visible;
            BottomInspectorPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            InspectorRow.Height = new GridLength(visible ? 0 : 225);
        }

        private void InspectorTab_Checked(object sender, RoutedEventArgs e)
        {
            if (GeneralTabView == null || ProgressTabView == null) return;
            GeneralTabView.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ProgressTabView.Visibility = TabProgress.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ResumeSelected_Click(object sender, RoutedEventArgs e)
        {
            if (DownloadsListView.SelectedItem is DownloadItem item && item.Progress < 100)
            {
                item.Status = "Downloading";
                item.SpeedText = "14.2 MB/s";
                RefreshFilter(SearchBox.Text);
            }
        }

        private void PauseSelected_Click(object sender, RoutedEventArgs e)
        {
            if (DownloadsListView.SelectedItem is DownloadItem item && item.Progress < 100)
            {
                item.Status = "Paused";
                item.SpeedText = "0 B/s";
                RefreshFilter(SearchBox.Text);
            }
        }

        private void StopAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in AllDownloads.Where(x => x.Status == "Downloading"))
            {
                item.Status = "Paused";
                item.SpeedText = "0 B/s";
            }
            RefreshFilter(SearchBox.Text);
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (DownloadsListView.SelectedItem is DownloadItem item)
            {
                AllDownloads.Remove(item);
                RefreshFilter(SearchBox.Text);
            }
        }

        private void ClearCompleted_Click(object sender, RoutedEventArgs e)
        {
            var done = AllDownloads.Where(x => x.Status == "Completed").ToList();
            foreach (var item in done) AllDownloads.Remove(item);
            RefreshFilter(SearchBox.Text);
        }

        private void DropZone_DragOver(object sender, DragEventArgs e)
            => e.AcceptedOperation = DataPackageOperation.Copy;

        private void DropZone_Drop(object sender, DragEventArgs e)
            => AddDownload_Click(sender, new RoutedEventArgs());
    }

    public class DownloadItem : INotifyPropertyChanged
    {
        public bool IsSelected { get; set; }
        public string FileName { get; set; } = "";
        public string QueueName { get; set; } = "Q1";
        public string SizeText { get; set; } = "";
        public string Status { get; set; } = "";
        public double Progress { get; set; }
        public string ProgressPercentage => $"{Progress:0}%";
        public string SpeedText { get; set; } = "";
        public string EtaText { get; set; } = "";
        public string AddedDate { get; set; } = "";
        public string Category { get; set; } = "All";
        public string Folder { get; set; } = @"D:\Downloads";
        public string IconGlyph { get; set; } = "\uE896";
        public string AccentColor { get; set; } = "#38BDF8";
        public string StatusColor { get; set; } = "#38BDF8";

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}