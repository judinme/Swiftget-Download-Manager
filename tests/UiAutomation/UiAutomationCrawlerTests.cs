using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using FluentAssertions;
using Xunit;

namespace SwiftGet.UiAutomation;

[Collection("UIAutomation")]
public class UiAutomationCrawlerTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _tempDataDir;
    private Application? _app;
    private UIA3Automation? _automation;

    public UiAutomationCrawlerTests()
    {
        _tempDataDir = Path.Combine(Path.GetTempPath(), "SwiftGet_QA_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataDir);
        Directory.CreateDirectory(@"d:\SwiftGet\qa-output\screenshots");
    }

    [Fact]
    public void CrawlMainWindow_DumpsFullControlTree_AndCapturesScreenshot()
    {
        var candidates = new[]
        {
            @"d:\SwiftGet\src\SwiftGet.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\SwiftGet.App.exe",
            @"d:\SwiftGet\src\SwiftGet.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\SwiftGet.App.exe",
            @"d:\SwiftGet\src\SwiftGet.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\SwiftGet.App.exe",
            @"d:\SwiftGet\src\SwiftGet.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\SwiftGet.App.exe"
        };
        var exePath = candidates.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? candidates[0];
        File.Exists(exePath).Should().BeTrue("SwiftGet.App.exe must exist");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--test-mode --data-dir \"{_tempDataDir}\"",
            UseShellExecute = true
        };

        _app = Application.Launch(psi);
        _automation = new UIA3Automation();

        Window? mainWindow = null;
        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(500);
            try
            {
                var windows = _app.GetAllTopLevelWindows(_automation);
                mainWindow = windows.FirstOrDefault(w => 
                    w.Title.Contains("SwiftGet", StringComparison.OrdinalIgnoreCase) || 
                    w.Name.Contains("SwiftGet", StringComparison.OrdinalIgnoreCase)) ?? windows.FirstOrDefault();
                if (mainWindow != null) break;
            }
            catch { }
        }

        mainWindow.Should().NotBeNull("MainWindow should be displayed within 15 seconds");

        // Give WinUI 3 a moment to layout controls
        Thread.Sleep(1500);

        // Capture screenshot
        try
        {
            using var screenshot = Capture.Element(mainWindow!);
            screenshot.ToFile(@"d:\SwiftGet\qa-output\screenshots\main_window_crawler.png");
        }
        catch
        {
            try
            {
                using var screenCapture = Capture.Screen();
                screenCapture.ToFile(@"d:\SwiftGet\qa-output\screenshots\main_window_crawler.png");
            }
            catch { }
        }

        // Crawl full control tree
        var rootNode = DumpElement(mainWindow!);
        var json = JsonSerializer.Serialize(rootNode, JsonOptions);
        File.WriteAllText(@"d:\SwiftGet\qa-output\control-tree.json", json);

        var sb = new StringBuilder();
        FormatNode(rootNode, 0, sb);
        File.WriteAllText(@"d:\SwiftGet\qa-output\control-tree.txt", sb.ToString());

        // Assert core controls are discovered
        var allIds = CollectAutomationIds(rootNode);
        allIds.Should().Contain("MainWindow_MenuBar");
        allIds.Should().Contain("Toolbar_AddDownload");
        allIds.Should().Contain("Sidebar_CategoryList");
        allIds.Should().Contain("Downloads_ListView");
        allIds.Should().Contain("Toolbar_SearchBox");
        allIds.Should().Contain("StatusBar_SnailMode");
    }

    private static UiElementNode DumpElement(AutomationElement element)
    {
        var node = new UiElementNode
        {
            ControlType = element.Properties.ControlType.ValueOrDefault.ToString(),
            Name = element.Properties.Name.ValueOrDefault ?? string.Empty,
            AutomationId = element.Properties.AutomationId.ValueOrDefault ?? string.Empty,
            ClassName = element.Properties.ClassName.ValueOrDefault ?? string.Empty,
            IsEnabled = element.Properties.IsEnabled.ValueOrDefault,
            IsOffscreen = element.Properties.IsOffscreen.ValueOrDefault
        };

        try
        {
            var children = element.FindAllChildren();
            foreach (var child in children)
            {
                node.Children.Add(DumpElement(child));
            }
        }
        catch
        {
            // Ignore access errors on dynamic child queries
        }

        return node;
    }

    private static void FormatNode(UiElementNode node, int depth, StringBuilder sb)
    {
        var indent = new string(' ', depth * 2);
        var idStr = string.IsNullOrWhiteSpace(node.AutomationId) ? "" : $" [Id: {node.AutomationId}]";
        var nameStr = string.IsNullOrWhiteSpace(node.Name) ? "" : $" \"{node.Name}\"";
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}- {node.ControlType}: {node.ClassName}{nameStr}{idStr} (Enabled: {node.IsEnabled})");
        foreach (var child in node.Children)
        {
            FormatNode(child, depth + 1, sb);
        }
    }

    private static List<string> CollectAutomationIds(UiElementNode node)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(node.AutomationId))
        {
            list.Add(node.AutomationId);
        }
        foreach (var child in node.Children)
        {
            list.AddRange(CollectAutomationIds(child));
        }
        return list;
    }

    public void Dispose()
    {
        try
        {
            _app?.Close();
            _app?.Dispose();
        }
        catch { }

        try
        {
            _automation?.Dispose();
        }
        catch { }

        try
        {
            if (Directory.Exists(_tempDataDir))
            {
                Directory.Delete(_tempDataDir, true);
            }
        }
        catch { }

        GC.SuppressFinalize(this);
    }
}

public class UiElementNode
{
    public string ControlType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AutomationId { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsOffscreen { get; set; }
    public List<UiElementNode> Children { get; set; } = new();
}
