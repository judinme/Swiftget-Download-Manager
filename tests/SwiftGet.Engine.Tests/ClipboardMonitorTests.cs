using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Clipboard;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class ClipboardMonitorTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly CategoryManager _categoryManager;

    public ClipboardMonitorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_clip_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _db = new SqliteDatabase(_dbPath);
        _categoryManager = new CategoryManager(_db);
    }

    public async ValueTask DisposeAsync()
    {
        _categoryManager.Dispose();
        await _db.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ProcessClipboardText_WithDownloadableUrl_ShouldFireUrlDetectedEvent()
    {
        await _db.InitializeAsync();
        await _categoryManager.InitializeAsync();

        var monitor = new ClipboardMonitor(_categoryManager);
        ClipboardUrlDetectedEventArgs? captured = null;
        monitor.UrlDetected += (s, e) => captured = e;

        monitor.ProcessClipboardText("Check out this build: https://downloads.example.org/builds/app_setup.exe thanks!");

        captured.Should().NotBeNull();
        captured!.Url.Should().Be("https://downloads.example.org/builds/app_setup.exe");
        captured.SuggestedFileName.Should().Be("app_setup.exe");
        captured.Category.Name.Should().Be("Programs");
    }

    [Fact]
    public async Task ProcessClipboardText_WithHtmlPageOrSearchDomain_ShouldNotFireEvent()
    {
        await _db.InitializeAsync();
        await _categoryManager.InitializeAsync();

        var monitor = new ClipboardMonitor(_categoryManager);
        var fired = false;
        monitor.UrlDetected += (s, e) => fired = true;

        monitor.ProcessClipboardText("https://google.com/search?q=swiftget");
        monitor.ProcessClipboardText("https://example.com/about.html");
        monitor.ProcessClipboardText("Random non-url text");

        fired.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessClipboardText_WhenDisabled_ShouldNotFireEvent()
    {
        await _db.InitializeAsync();
        await _categoryManager.InitializeAsync();

        var monitor = new ClipboardMonitor(_categoryManager)
        {
            IsEnabled = false
        };
        var fired = false;
        monitor.UrlDetected += (s, e) => fired = true;

        monitor.ProcessClipboardText("https://example.com/archive.zip");
        fired.Should().BeFalse();
    }
}
