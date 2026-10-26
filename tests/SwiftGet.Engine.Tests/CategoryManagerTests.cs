using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class CategoryManagerTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"swiftget_cat_test_{Guid.NewGuid():N}.db");
    private SqliteDatabase? _db;
    private CategoryManager? _manager;

    public async Task InitializeAsync()
    {
        _db = new SqliteDatabase(_dbPath);
        await _db.InitializeAsync();
        _manager = new CategoryManager(_db);
        await _manager.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        _manager?.Dispose();
        if (_db != null)
        {
            await _db.DisposeAsync();
        }

        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var walFile = _dbPath + "-wal";
            var shmFile = _dbPath + "-shm";
            if (File.Exists(walFile)) File.Delete(walFile);
            if (File.Exists(shmFile)) File.Delete(shmFile);
        }
        catch { }
    }

    [Theory]
    [InlineData("archive.zip", "Compressed")]
    [InlineData("setup.exe", "Programs")]
    [InlineData("song.flac", "Music")]
    [InlineData("movie.mkv", "Video")]
    [InlineData("document.pdf", "Documents")]
    [InlineData("random.xyz123", "General")]
    public void ResolveCategory_ByExtension_ResolvesCorrectCategory(string fileName, string expectedCategoryName)
    {
        var category = _manager!.ResolveCategory(fileName);
        category.Should().NotBeNull();
        category.Name.Should().Be(expectedCategoryName);
    }

    [Fact]
    public void ResolveCategory_ByMimeTypeFallback_ResolvesCorrectCategory()
    {
        var category = _manager!.ResolveCategory("unnamed_stream", "video/mp4; codecs=\"avc1\"");
        category.Should().NotBeNull();
        category.Name.Should().Be("Video");
    }

    [Fact]
    public void ResolveDestinationPath_ExpandsEnvironmentVariablesAndSanitizes()
    {
        var category = new CategoryRule
        {
            Name = "Documents",
            TargetDirectory = "%TEMP%\\SwiftGet_CategoryTest"
        };

        var path = _manager!.ResolveDestinationPath("..\\..\\malicious:report?.pdf", category);
        path.Should().NotBeNull();
        Path.GetFileName(path).Should().Be("____maliciousreport.pdf");
        Path.GetDirectoryName(path)!.Should().Contain("SwiftGet_CategoryTest");

        try
        {
            if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }
        catch { }
    }
}
