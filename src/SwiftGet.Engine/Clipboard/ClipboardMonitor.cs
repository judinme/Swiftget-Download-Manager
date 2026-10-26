using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Categories;

namespace SwiftGet.Engine.Clipboard;

public sealed class ClipboardUrlDetectedEventArgs : EventArgs
{
    public string Url { get; }
    public string SuggestedFileName { get; }
    public CategoryRule Category { get; }

    public ClipboardUrlDetectedEventArgs(string url, string suggestedFileName, CategoryRule category)
    {
        Url = url;
        SuggestedFileName = suggestedFileName;
        Category = category;
    }
}

public interface IClipboardMonitor
{
    bool IsEnabled { get; set; }
    event EventHandler<ClipboardUrlDetectedEventArgs>? UrlDetected;
    void ProcessClipboardText(string? text);
}

public sealed partial class ClipboardMonitor : IClipboardMonitor
{
    private readonly ICategoryManager _categoryManager;
    private string? _lastProcessedUrl;
    private readonly HashSet<string> _excludedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "google.com", "bing.com", "duckduckgo.com", "facebook.com", "twitter.com", "x.com", "instagram.com"
    };

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public bool IsEnabled { get; set; } = true;

    public event EventHandler<ClipboardUrlDetectedEventArgs>? UrlDetected;

    public ClipboardMonitor(ICategoryManager categoryManager)
    {
        _categoryManager = categoryManager ?? throw new ArgumentNullException(nameof(categoryManager));
    }

    public void ProcessClipboardText(string? text)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var matches = UrlRegex().Matches(text);
        foreach (Match match in matches)
        {
            var rawUrl = match.Value.Trim();
            if (string.Equals(rawUrl, _lastProcessedUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (_excludedDomains.Contains(uri.Host) || _excludedDomains.Contains(GetRootDomain(uri.Host)))
            {
                continue;
            }

            var fileName = Path.GetFileName(uri.LocalPath);
            var ext = Path.GetExtension(fileName);

            // A downloadable link should either have a recognized file extension or be a file resource
            if (string.IsNullOrEmpty(ext) || ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _lastProcessedUrl = rawUrl;
            var category = _categoryManager.ResolveCategory(fileName);
            UrlDetected?.Invoke(this, new ClipboardUrlDetectedEventArgs(rawUrl, fileName, category));
        }
    }

    private static string GetRootDomain(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        return string.Join('.', parts[^2..]);
    }
}
