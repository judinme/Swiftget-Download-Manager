using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.IO;
using SwiftGet.Engine.Security;

namespace SwiftGet.Engine.Categories;

public class CategoryManager : ICategoryManager, IDisposable
{
    private readonly ISwiftGetDatabase _db;
    private List<CategoryRule> _categories = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public CategoryManager(ISwiftGetDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await _db.GetAllCategoriesAsync(ct).ConfigureAwait(false);
            _categories = [.. list];
        }
        finally
        {
            _lock.Release();
        }
    }

    public CategoryRule ResolveCategory(string fileName, string? contentType = null)
    {
        var ext = Path.GetExtension(fileName);

        // 1. Match by extension first (non-default specific categories first)
        foreach (var cat in _categories.Where(c => !c.IsDefault))
        {
            if (cat.MatchesExtension(ext))
            {
                return cat;
            }
        }

        // 2. Fallback to MIME type if provided
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var cleanMime = contentType.Split(';')[0].Trim().ToLowerInvariant();
            if (cleanMime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                var videoCat = _categories.FirstOrDefault(c => c.Name.Equals("Video", StringComparison.OrdinalIgnoreCase));
                if (videoCat != null) return videoCat;
            }
            if (cleanMime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                var audioCat = _categories.FirstOrDefault(c => c.Name.Equals("Music", StringComparison.OrdinalIgnoreCase));
                if (audioCat != null) return audioCat;
            }
            if (cleanMime == "application/pdf" || cleanMime.Contains("document") || cleanMime.Contains("spreadsheet"))
            {
                var docCat = _categories.FirstOrDefault(c => c.Name.Equals("Documents", StringComparison.OrdinalIgnoreCase));
                if (docCat != null) return docCat;
            }
            if (cleanMime.Contains("zip") || cleanMime.Contains("tar") || cleanMime.Contains("compressed") || cleanMime.Contains("archive"))
            {
                var compCat = _categories.FirstOrDefault(c => c.Name.Equals("Compressed", StringComparison.OrdinalIgnoreCase));
                if (compCat != null) return compCat;
            }
        }

        // 3. Fallback to default category
        var defaultCat = _categories.FirstOrDefault(c => c.IsDefault)
            ?? _categories.FirstOrDefault(c => c.Name.Equals("General", StringComparison.OrdinalIgnoreCase))
            ?? new CategoryRule
            {
                Id = "general",
                Name = "General",
                TargetDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                FileExtensions = "*",
                IsDefault = true
            };

        return defaultCat;
    }

    public string ResolveDestinationPath(string fileName, CategoryRule category)
    {
        var sanitizedFileName = FileNameSanitizer.Sanitize(fileName);
        var rawTarget = !string.IsNullOrWhiteSpace(category.TargetDirectory)
            ? category.TargetDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        var safePath = PathTraversalGuard.EnsureSafePath(rawTarget, sanitizedFileName);
        var safeDir = Path.GetDirectoryName(safePath);

        if (!string.IsNullOrWhiteSpace(safeDir))
        {
            try
            {
                if (!Directory.Exists(safeDir))
                {
                    Directory.CreateDirectory(safeDir);
                }
            }
            catch
            {
                var fallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(fallbackDir);
                return PathTraversalGuard.EnsureSafePath(fallbackDir, sanitizedFileName);
            }
        }

        return safePath;
    }

    public async Task<IReadOnlyList<CategoryRule>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _categories.AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveCategoryAsync(CategoryRule category, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _db.SaveCategoryAsync(category, ct).ConfigureAwait(false);
            var list = await _db.GetAllCategoriesAsync(ct).ConfigureAwait(false);
            _categories = [.. list];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteCategoryAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _db.DeleteCategoryAsync(id, ct).ConfigureAwait(false);
            var list = await _db.GetAllCategoriesAsync(ct).ConfigureAwait(false);
            _categories = [.. list];
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _lock.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
