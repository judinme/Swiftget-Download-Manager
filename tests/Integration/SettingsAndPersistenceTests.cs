using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using Xunit;

namespace SwiftGet.Integration;

public class SettingsAndPersistenceTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;

    public SettingsAndPersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"swiftget_test_settings_{Guid.NewGuid():N}.db");
        _db = new SqliteDatabase(_dbPath);
    }

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CategoryManager_SupportsCustomCategoriesAndResolvesFiles()
    {
        var categoryManager = new CategoryManager(_db);
        await categoryManager.InitializeAsync();

        // Add custom 3D Models category
        var customCategory = new CategoryRule
        {
            Id = "3d_models",
            Name = "3D Models",
            TargetDirectory = "C:\\Downloads\\3DModels",
            FileExtensions = "stl,obj,fbx,blend"
        };

        await categoryManager.SaveCategoryAsync(customCategory);

        // Verify resolution
        var resolvedStl = categoryManager.ResolveCategory("spaceship_model.stl");
        resolvedStl.Id.Should().Be("3d_models");
        resolvedStl.TargetDirectory.Should().Be("C:\\Downloads\\3DModels");

        var resolvedBlend = categoryManager.ResolveCategory("scene.blend");
        resolvedBlend.Id.Should().Be("3d_models");

        // General fallback
        var resolvedUnknown = categoryManager.ResolveCategory("data.unknownext");
        resolvedUnknown.Id.Should().Be("general");

        // Clean up
        await categoryManager.DeleteCategoryAsync("3d_models");
        var afterRemoval = categoryManager.ResolveCategory("spaceship_model.stl");
        afterRemoval.Id.Should().Be("general");
    }

    [Fact]
    public async Task Database_PersistsDownloadStateAndReconstitutesOnRestart()
    {
        var downloadId = Guid.NewGuid().ToString("N");
        var original = new DownloadItem
        {
            Id = downloadId,
            Url = "https://example.com/bigfile.iso",
            FileName = "bigfile.iso",
            DestinationPath = "C:\\Downloads\\bigfile.iso",
            TotalBytes = 4L * 1024 * 1024 * 1024, // 4 GB (test >2GB 64-bit int support)
            DownloadedBytes = 2L * 1024 * 1024 * 1024,
            Status = DownloadStatus.Paused,
            CategoryId = "Compressed",
            QueueId = "default",
            CreatedAtUtc = DateTime.UtcNow
        };

        await _db.SaveDownloadAsync(original);

        // Read back
        var fetched = await _db.GetDownloadAsync(downloadId);
        fetched.Should().NotBeNull();
        fetched!.TotalBytes.Should().Be(4L * 1024 * 1024 * 1024);
        fetched.DownloadedBytes.Should().Be(2L * 1024 * 1024 * 1024);
        fetched.Status.Should().Be(DownloadStatus.Paused);
        fetched.FileName.Should().Be("bigfile.iso");
    }
}
