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
    private readonly SwiftGet.Core.IO.IFileNameResolver _fileNameResolver;
    private string? _lastProcessedUrl;
    private readonly HashSet<string> _excludedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "google.com", "bing.com", "duckduckgo.com", "facebook.com", "twitter.com", "x.com", "instagram.com"
    };

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"magnet:\?xt=urn:btih:[a-zA-Z0-9]+[^\s""'<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetRegex();

    [GeneratedRegex(@"dn=([^&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayNameRegex();

    public bool IsEnabled { get; set; } = true;

    public event EventHandler<ClipboardUrlDetectedEventArgs>? UrlDetected;

    public ClipboardMonitor(ICategoryManager categoryManager, SwiftGet.Core.IO.IFileNameResolver? fileNameResolver = null)
    {
        _categoryManager = categoryManager ?? throw new ArgumentNullException(nameof(categoryManager));
        _fileNameResolver = fileNameResolver ?? new SwiftGet.Core.IO.FileNameResolver();
    }

    public void ProcessClipboardText(string? text)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // 1. Check for Magnet Links
        var magnetMatches = MagnetRegex().Matches(text);
        foreach (Match match in magnetMatches)
        {
            var rawMagnet = match.Value.Trim();
            if (string.Equals(rawMagnet, _lastProcessedUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _lastProcessedUrl = rawMagnet;
            string displayName = "Torrent";
            var dnMatch = DisplayNameRegex().Match(rawMagnet);
            if (dnMatch.Success)
            {
                displayName = Uri.UnescapeDataString(dnMatch.Groups[1].Value).Replace('+', ' ');
            }

            var category = _categoryManager.ResolveCategory(displayName.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ? displayName : $"{displayName}.torrent");
            if (string.Equals(category.Name, "General", StringComparison.OrdinalIgnoreCase))
            {
                category = new CategoryRule
                {
                    Id = "torrents",
                    Name = "Torrents",
                    TargetDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    FileExtensions = "torrent",
                    IconKey = "Torrent"
                };
            }
            UrlDetected?.Invoke(this, new ClipboardUrlDetectedEventArgs(rawMagnet, displayName, category));
            return;
        }

        // 2. Check for HTTP/HTTPS URLs
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

            var fileName = _fileNameResolver.ResolveInitialCandidate(rawUrl);
            var ext = _fileNameResolver.GetCompoundExtension(fileName);

            // A downloadable link should either have a recognized file extension or be a file resource
            if (string.IsNullOrEmpty(ext) || ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _lastProcessedUrl = rawUrl;
            var category = _categoryManager.ResolveCategory(fileName);
            if (string.Equals(category.Name, "General", StringComparison.OrdinalIgnoreCase) && ext.Equals(".torrent", StringComparison.OrdinalIgnoreCase))
            {
                category = new CategoryRule
                {
                    Id = "torrents",
                    Name = "Torrents",
                    TargetDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    FileExtensions = "torrent",
                    IconKey = "Torrent"
                };
            }
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
