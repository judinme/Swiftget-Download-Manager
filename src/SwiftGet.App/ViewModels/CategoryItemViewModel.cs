using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SwiftGet.Core.Models;

namespace SwiftGet.App.ViewModels;

public sealed partial class CategoryItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _iconGlyph = "\uE8B7"; // Default folder glyph

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string _categoryKey = "all";

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<CategoryItemViewModel> Children { get; } = [];

    public CategoryItemViewModel(string name, string iconGlyph, string categoryKey)
    {
        _name = name;
        _iconGlyph = iconGlyph;
        _categoryKey = categoryKey;
    }

    public bool Matches(DownloadItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return CategoryKey switch
        {
            "all" => true,
            "unfinished" => item.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Queued or DownloadStatus.FetchingMetadata or DownloadStatus.Checking or DownloadStatus.Failed,
            "finished" => item.Status is DownloadStatus.Completed or DownloadStatus.Seeding,
            "torrents" => item.JobKind == JobKind.Torrent || string.Equals(item.Category, "torrents", StringComparison.OrdinalIgnoreCase) || MatchesExtension(item.FileName, [".torrent"]),
            "grabber" => item.IsGrabberProject,
            "queues" => !string.IsNullOrEmpty(item.QueueName),
            "queue_main" => string.Equals(item.QueueName, "Main", StringComparison.OrdinalIgnoreCase) || string.Equals(item.QueueName, "default", StringComparison.OrdinalIgnoreCase),
            "queue_sync" => string.Equals(item.QueueName, "Sync", StringComparison.OrdinalIgnoreCase),
            "compressed" => string.Equals(item.EffectiveCategory, "compressed", StringComparison.OrdinalIgnoreCase) || MatchesExtension(item.FileName, [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".zst", ".iso", ".tgz"]),
            "documents" => string.Equals(item.EffectiveCategory, "documents", StringComparison.OrdinalIgnoreCase) || MatchesExtension(item.FileName, [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".epub", ".csv", ".json", ".xml"]),
            "music" => string.Equals(item.EffectiveCategory, "music", StringComparison.OrdinalIgnoreCase) || MatchesExtension(item.FileName, [".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".wma", ".opus"]),
            "programs" => string.Equals(item.EffectiveCategory, "programs", StringComparison.OrdinalIgnoreCase) || MatchesExtension(item.FileName, [".exe", ".msi", ".bat", ".cmd", ".ps1", ".apk", ".appx", ".msix", ".dmg"]),
            "video" => item.JobKind == JobKind.Media || string.Equals(item.EffectiveCategory, "video", StringComparison.OrdinalIgnoreCase) || MatchesExtension(item.FileName, [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".ts", ".m4v"]),
            _ => true
        };
    }

    private static bool MatchesExtension(string fileName, string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return Array.IndexOf(extensions, ext) >= 0;
    }
}
