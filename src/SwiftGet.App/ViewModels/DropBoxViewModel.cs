using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;

namespace SwiftGet.App.ViewModels;

public sealed partial class DropBoxViewModel : ObservableObject
{
    private readonly ISwiftGetDatabase _db;
    private readonly ICategoryManager _categoryManager;

    [ObservableProperty]
    private int _droppedCount;

    [ObservableProperty]
    private string _lastItemAdded = "Drop links or files here";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isDraggingOver;

    public DropBoxViewModel(ISwiftGetDatabase db, ICategoryManager categoryManager)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _categoryManager = categoryManager ?? throw new ArgumentNullException(nameof(categoryManager));
    }

    public async Task<int> HandleDroppedTextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var added = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            {
                var fileName = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = "download_" + DateTime.UtcNow.Ticks + ".bin";
                }

                var category = _categoryManager.ResolveCategory(fileName);
                var destPath = Path.Combine(category.TargetDirectory, fileName);

                var item = new DownloadItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Url = uri.AbsoluteUri,
                    FileName = fileName,
                    DestinationPath = destPath,
                    CategoryId = category.Id,
                    QueueId = "default",
                    Status = DownloadStatus.Queued,
                    CreatedAtUtc = DateTime.UtcNow
                };

                await _db.SaveDownloadAsync(item, ct).ConfigureAwait(false);
                added++;
                DroppedCount++;
                LastItemAdded = fileName;
                StatusText = $"Enqueued {fileName}";
            }
        }

        return added;
    }

    [RelayCommand]
    public void ResetCounter()
    {
        DroppedCount = 0;
        StatusText = "Counter reset";
    }
}
