using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using FluentAssertions;
using Xunit;

namespace SwiftGet.UiAutomation;

[Collection("UIAutomation")]
public class UiAutomationInteractionTests : IDisposable
{
    private readonly string _tempDataDir;
    private Application? _app;
    private UIA3Automation? _automation;

    public UiAutomationInteractionTests()
    {
        _tempDataDir = Path.Combine(Path.GetTempPath(), "SwiftGet_QA_Interact_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataDir);
        Directory.CreateDirectory(@"d:\SwiftGet\qa-output\screenshots");
    }

    [Fact]
    public void MainWindow_InteractiveWorkflow_PerformsCategorySearchAndInspectorNavigation()
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
            catch
            {
                // COM apartment initialization jitter
            }
        }

        mainWindow.Should().NotBeNull("MainWindow should appear within 15 seconds");
        Thread.Sleep(1500);

        // 1. Test Category Selection via UIA SelectionItem / Invoke pattern
        var catCompressed = mainWindow!.FindFirstDescendant(cf => cf.ByAutomationId("Category_Compressed"));
        if (catCompressed != null)
        {
            var selectPattern = catCompressed.Patterns.SelectionItem.PatternOrDefault;
            if (selectPattern != null)
            {
                selectPattern.Select();
            }
            else
            {
                catCompressed.Patterns.Invoke.PatternOrDefault?.Invoke();
            }
            Thread.Sleep(400);
            CaptureScreenshot(mainWindow, @"d:\SwiftGet\qa-output\screenshots\interaction_category_switch.png");
        }

        // Return to All Downloads
        var catAll = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Category_All"));
        catAll?.Patterns.SelectionItem.PatternOrDefault?.Select();
        Thread.Sleep(300);

        // 2. Test Search Box via UIA Value pattern
        var searchBox = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Search_Box"));
        if (searchBox != null)
        {
            searchBox.Patterns.Value.PatternOrDefault?.SetValue("ubuntu.iso");
            Thread.Sleep(400);
            CaptureScreenshot(mainWindow, @"d:\SwiftGet\qa-output\screenshots\interaction_search_filter.png");
            searchBox.Patterns.Value.PatternOrDefault?.SetValue(string.Empty);
        }

        // 3. Test Inspector Tab Switching via SelectionItem / Invoke pattern
        var tabFiles = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Inspector_TabFiles"));
        if (tabFiles != null)
        {
            var selectTab = tabFiles.Patterns.SelectionItem.PatternOrDefault;
            if (selectTab != null)
            {
                selectTab.Select();
            }
            else
            {
                tabFiles.Patterns.Invoke.PatternOrDefault?.Invoke();
            }
            Thread.Sleep(400);
            CaptureScreenshot(mainWindow, @"d:\SwiftGet\qa-output\screenshots\interaction_inspector_tabs.png");
        }

        var tabGeneral = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Inspector_TabGeneral"));
        tabGeneral?.Patterns.SelectionItem.PatternOrDefault?.Select();
        Thread.Sleep(300);

        // 4. Test Snail Mode Toggle via Toggle pattern
        var snailToggle = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Toolbar_SnailMode"));
        if (snailToggle != null)
        {
            var togglePattern = snailToggle.Patterns.Toggle.PatternOrDefault;
            if (togglePattern != null)
            {
                togglePattern.Toggle();
                Thread.Sleep(300);
                togglePattern.Toggle();
            }
        }
    }

    private static void CaptureScreenshot(Window window, string filePath)
    {
        try
        {
            var screenshot = Capture.Rectangle(window.BoundingRectangle);
            screenshot.ToFile(filePath);
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            if (_app != null && !_app.HasExited)
            {
                _app.Close();
                _app.Dispose();
            }
        }
        catch { }

        _automation?.Dispose();

        try
        {
            if (Directory.Exists(_tempDataDir)) Directory.Delete(_tempDataDir, true);
        }
        catch { }

        GC.SuppressFinalize(this);
    }
}
