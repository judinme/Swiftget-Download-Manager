using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;

namespace SwiftGet.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly List<DownloadItemViewModel> _masterList = [];

    public IReadOnlyList<DownloadItemViewModel> AllDownloads => _masterList;

    [ObservableProperty]
    private ObservableCollection<DownloadItemViewModel> _filteredDownloads = [];

    [ObservableProperty]
    private ObservableCollection<CategoryItemViewModel> _categories = [];

    [ObservableProperty]
    private CategoryItemViewModel? _selectedCategory;

    [ObservableProperty]
    private DownloadItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private int _totalDownloadsCount;

    [ObservableProperty]
    private int _activeDownloadsCount;

    [ObservableProperty]
    private string _totalTransferRate = "-";

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _freeDiskSpace = string.Empty;

    private readonly ISwiftGetDatabase? _db;

    public Func<DownloadItemViewModel, Task>? ResumeHandler { get; set; }
    public Action<DownloadItemViewModel>? StopHandler { get; set; }
    public Action? StopAllHandler { get; set; }
    public Func<DownloadItemViewModel, Task>? DeleteHandler { get; set; }

    public MainViewModel(ISwiftGetDatabase? db = null)
    {
        _db = db;
        InitializeCategories();
        UpdateFreeDiskSpace();
    }

    private void InitializeCategories()
    {
        CategoryItemViewModel allDownloads = new("All Downloads", "\uE8B7", "all");
        allDownloads.Children.Add(new CategoryItemViewModel("Compressed", "\uE7B8", "compressed"));
        allDownloads.Children.Add(new CategoryItemViewModel("Documents", "\uE8A5", "documents"));
        allDownloads.Children.Add(new CategoryItemViewModel("Music", "\uE8D6", "music"));
        allDownloads.Children.Add(new CategoryItemViewModel("Programs", "\uE7B5", "programs"));
        allDownloads.Children.Add(new CategoryItemViewModel("Video", "\uE714", "video"));
        allDownloads.Children.Add(new CategoryItemViewModel("Torrents", "\uE896", "torrents"));

        CategoryItemViewModel unfinished = new("Unfinished", "\uE895", "unfinished");
        CategoryItemViewModel finished = new("Finished", "\uE73E", "finished");
        CategoryItemViewModel grabber = new("Grabber projects", "\uE774", "grabber");

        CategoryItemViewModel queues = new("Queues", "\uE8A7", "queues");
        queues.Children.Add(new CategoryItemViewModel("Main download queue", "\uE768", "queue_main"));
        queues.Children.Add(new CategoryItemViewModel("Synchronization queue", "\uE895", "queue_sync"));

        Categories = [allDownloads, unfinished, finished, grabber, queues];
        SelectedCategory = allDownloads;
    }

    partial void OnSelectedCategoryChanged(CategoryItemViewModel? value)
    {
        ApplyFilter();
    }

    partial void OnSearchFilterChanged(string value)
    {
        ApplyFilter();
    }

    public void SelectCategory(string categoryKey)
    {
        CategoryItemViewModel? found = FindCategory(Categories, categoryKey);
        if (found != null)
        {
            SelectedCategory = found;
        }
    }

    private static CategoryItemViewModel? FindCategory(IEnumerable<CategoryItemViewModel> categories, string key)
    {
        foreach (CategoryItemViewModel cat in categories)
        {
            if (string.Equals(cat.CategoryKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return cat;
            }

            CategoryItemViewModel? child = FindCategory(cat.Children, key);
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    public void AddDownload(DownloadItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var existing = _masterList.FirstOrDefault(x => x.Id == item.Id);
        if (existing != null)
        {
            _masterList.Remove(existing);
            FilteredDownloads.Remove(existing);
        }
        _masterList.Add(item);
        UpdateMetrics();

        if (MatchesFilter(item))
        {
            FilteredDownloads.Add(item);
        }
    }

    public void AddDownloads(IEnumerable<DownloadItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _masterList.AddRange(items);
        UpdateMetrics();
        ApplyFilter();
    }

    public void RemoveDownload(DownloadItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _masterList.Remove(item);
        FilteredDownloads.Remove(item);
        UpdateMetrics();
    }

    public void ClearAll()
    {
        _masterList.Clear();
        FilteredDownloads.Clear();
        UpdateMetrics();
    }

    public void ApplyFilter()
    {
        CategoryItemViewModel activeCategory = SelectedCategory ?? Categories[0];
        string query = SearchFilter.Trim();

        IEnumerable<DownloadItemViewModel> queryable = _masterList;

        if (activeCategory.CategoryKey != "all")
        {
            queryable = queryable.Where(activeCategory.Matches);
        }

        if (!string.IsNullOrEmpty(query))
        {
            queryable = queryable.Where(d => d.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                             d.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                             d.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        FilteredDownloads = new ObservableCollection<DownloadItemViewModel>(queryable);
    }

    private bool MatchesFilter(DownloadItemViewModel item)
    {
        CategoryItemViewModel activeCategory = SelectedCategory ?? Categories[0];
        if (!activeCategory.Matches(item))
        {
            return false;
        }

        string query = SearchFilter.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public void UpdateMetrics()
    {
        TotalDownloadsCount = _masterList.Count;
        int active = 0;
        double speed = 0;

        foreach (DownloadItemViewModel d in _masterList)
        {
            if (d.Status is DownloadStatus.Downloading or DownloadStatus.FetchingMetadata or DownloadStatus.Checking or DownloadStatus.Seeding)
            {
                active++;
                speed += d.SpeedBytesPerSec + d.UploadSpeedBytesPerSec;
            }
        }

        ActiveDownloadsCount = active;

        TotalTransferRate = speed > 0
            ? DownloadItemViewModel.FormatSpeed(speed)
            : (active > 0 ? "0 B/s" : "-");

        UpdateCategoryCounts();
    }

    private void UpdateCategoryCounts()
    {
        foreach (CategoryItemViewModel cat in Categories)
        {
            cat.Count = _masterList.Count(cat.Matches);
            foreach (CategoryItemViewModel child in cat.Children)
            {
                child.Count = _masterList.Count(child.Matches);
            }
        }
    }

    public void UpdateFreeDiskSpace()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "C:\\";
            DriveInfo drive = new(root);
            FreeDiskSpace = string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB free", drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0));
        }
        catch
        {
            FreeDiskSpace = "Drive info unavailable";
        }
    }

    [RelayCommand]
    private async Task ResumeSelectedAsync()
    {
        if (SelectedItem != null)
        {
            if (ResumeHandler != null)
            {
                await ResumeHandler.Invoke(SelectedItem);
            }
            else if (SelectedItem.Status is DownloadStatus.Paused or DownloadStatus.Queued)
            {
                SelectedItem.Status = DownloadStatus.Downloading;
            }
            UpdateMetrics();
        }
    }

    [RelayCommand]
    private void StopSelected()
    {
        if (SelectedItem != null)
        {
            StopHandler?.Invoke(SelectedItem);
            SelectedItem.Status = DownloadStatus.Paused;
            SelectedItem.SpeedBytesPerSec = 0;
            UpdateMetrics();
        }
    }

    [RelayCommand]
    private void StopAll()
    {
        StopAllHandler?.Invoke();
        foreach (DownloadItemViewModel d in _masterList.Where(d => d.Status == DownloadStatus.Downloading))
        {
            d.Status = DownloadStatus.Paused;
            d.SpeedBytesPerSec = 0;
        }

        UpdateMetrics();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedItem != null)
        {
            var item = SelectedItem;
            if (DeleteHandler != null)
            {
                await DeleteHandler.Invoke(item);
            }
            RemoveDownload(item);
            SelectedItem = null;
            if (_db != null)
            {
                await _db.DeleteDownloadAsync(item.Id.ToString("N"));
            }
        }
    }

    [RelayCommand]
    private async Task DeleteCompletedAsync()
    {
        List<DownloadItemViewModel> completed = _masterList.Where(d => d.Status == DownloadStatus.Completed).ToList();
        foreach (DownloadItemViewModel d in completed)
        {
            if (DeleteHandler != null)
            {
                await DeleteHandler.Invoke(d);
            }
            _masterList.Remove(d);
            FilteredDownloads.Remove(d);
            if (_db != null)
            {
                await _db.DeleteDownloadAsync(d.Id.ToString("N"));
            }
        }
        if (SelectedItem != null && !_masterList.Contains(SelectedItem))
        {
            SelectedItem = null;
        }

        UpdateMetrics();
    }
}
