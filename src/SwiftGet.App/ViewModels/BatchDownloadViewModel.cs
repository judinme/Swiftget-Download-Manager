using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwiftGet.Core.IO;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Batch;
using SwiftGet.Engine.Categories;

namespace SwiftGet.App.ViewModels;

public sealed partial class BatchDownloadViewModel : ObservableObject
{
    private readonly ISwiftGetDatabase _db;
    private readonly ICategoryManager _categoryManager;
    private readonly IFileNameResolver _fileNameResolver;

    [ObservableProperty]
    private string _addressPattern = "https://example.com/files/img_[001-010].jpg";

    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _generatedUrls = [];

    [ObservableProperty]
    private string _queueName = "default";

    [ObservableProperty]
    private string _statusMessage = "Enter a pattern and click Preview.";

    [ObservableProperty]
    private int _generatedCount;

    [ObservableProperty]
    private string _generatedCountSummary = "Generated URLs (0):";

    public BatchDownloadViewModel(ISwiftGetDatabase db, ICategoryManager categoryManager, IFileNameResolver? fileNameResolver = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _categoryManager = categoryManager ?? throw new ArgumentNullException(nameof(categoryManager));
        _fileNameResolver = fileNameResolver ?? new SwiftGet.Core.IO.FileNameResolver();
        _saveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        UpdatePreview();
    }

    partial void OnAddressPatternChanged(string value)
    {
        UpdatePreview();
    }

    [RelayCommand]
    public void UpdatePreview()
    {
        if (BatchPatternParser.IsValidPattern(AddressPattern, out var error))
        {
            var urls = BatchPatternParser.GenerateUrls(AddressPattern);
            GeneratedUrls.Clear();
            foreach (var url in urls)
            {
                GeneratedUrls.Add(url);
            }
            GeneratedCount = GeneratedUrls.Count;
            GeneratedCountSummary = $"Generated URLs ({GeneratedCount}):";
            StatusMessage = $"Ready to add {GeneratedCount} downloads.";
        }
        else
        {
            GeneratedUrls.Clear();
            GeneratedCount = 0;
            GeneratedCountSummary = "Generated URLs (0):";
            StatusMessage = error ?? "Invalid pattern syntax.";
        }
    }

    public async Task<int> AddBatchDownloadsAsync(CancellationToken ct = default)
    {
        if (GeneratedUrls.Count == 0)
        {
            UpdatePreview();
        }

        var count = 0;
        foreach (var url in GeneratedUrls)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = _fileNameResolver.ResolveInitialCandidate(url);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "download.bin";

            var category = _categoryManager.ResolveCategory(fileName);
            var targetDir = string.IsNullOrWhiteSpace(SaveDirectory) ? category.TargetDirectory : SaveDirectory;
            var destPath = Path.Combine(targetDir, fileName);

            var item = new DownloadItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Url = url,
                FileName = fileName,
                DestinationPath = destPath,
                QueueId = QueueName,
                CategoryId = category.Id,
                Status = DownloadStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow
            };

            await _db.SaveDownloadAsync(item, ct).ConfigureAwait(false);
            count++;
        }

        StatusMessage = $"Successfully enqueued {count} downloads.";
        return count;
    }
}
