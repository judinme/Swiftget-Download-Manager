using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SwiftGet.Core.Localization;

public sealed class LocalizationService : ILocalizationService
{
    private string _currentLanguage = "en-US";
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _catalogs = new(StringComparer.OrdinalIgnoreCase);

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set => _currentLanguage = string.IsNullOrWhiteSpace(value) ? "en-US" : value;
    }

    public IReadOnlyList<string> SupportedLanguages => ["en-US", "es-ES", "de-DE", "fr-FR", "zh-CN", "ja-JP"];

    public LocalizationService()
    {
        SeedDefaultEnglishCatalog();
    }

    public string GetString(string key, string? defaultText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_catalogs.TryGetValue(_currentLanguage, out var langDict) && langDict.TryGetValue(key, out var val))
        {
            return val;
        }

        // Fallback to en-US
        if (_catalogs.TryGetValue("en-US", out var enDict) && enDict.TryGetValue(key, out var enVal))
        {
            return enVal;
        }

        return defaultText ?? key;
    }

    public void RegisterString(string language, string key, string value)
    {
        var dict = _catalogs.GetOrAdd(language, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        dict[key] = value;
    }

    private void SeedDefaultEnglishCatalog()
    {
        var en = _catalogs.GetOrAdd("en-US", _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        en["App.Title"] = "SwiftGet Download Manager";
        en["Tasks.AddDownload"] = "Add new download...";
        en["Tasks.MediaDownload"] = "Download video / streaming media...";
        en["Tasks.BatchDownload"] = "Batch download...";
        en["Tasks.SiteGrabber"] = "Run site grabber...";
        en["Tasks.Export"] = "Export download list...";
        en["Tasks.Import"] = "Import download list...";
        en["Tasks.Exit"] = "Exit";
        en["Downloads.StartQueue"] = "Start queue";
        en["Downloads.StopQueue"] = "Stop queue";
        en["Downloads.Options"] = "Speed limiter options...";
        en["Downloads.Scheduler"] = "Scheduler...";
        en["Help.Diagnostics"] = "Export diagnostic report...";
        en["Help.About"] = "About SwiftGet";
        en["Common.Download"] = "Download";
        en["Common.Cancel"] = "Cancel";
        en["Common.Delete"] = "Delete";
        en["Common.Resume"] = "Resume";
        en["Common.Stop"] = "Stop";
        en["Common.OK"] = "OK";
    }
}
