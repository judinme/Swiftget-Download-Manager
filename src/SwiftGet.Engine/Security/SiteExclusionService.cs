using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SwiftGet.Engine.Security;

public interface ISiteExclusionService
{
    IReadOnlyCollection<string> ExcludedDomains { get; }
    bool IsExcluded(string? urlOrHost);
    void AddExclusion(string domain);
    void RemoveExclusion(string domain);
    void LoadFromFile(string filePath);
}

public sealed class SiteExclusionService : ISiteExclusionService
{
    private static readonly string[] DefaultDomains =
    [
        "google.com", "accounts.google.com", "apis.google.com", "gstatic.com",
        "bing.com", "duckduckgo.com", "yahoo.com",
        "live.com", "login.live.com", "microsoft.com", "login.microsoftonline.com",
        "apple.com", "icloud.com",
        "facebook.com", "twitter.com", "x.com", "instagram.com", "linkedin.com",
        "netflix.com", "spotify.com", "github.com",
        "bankofamerica.com", "chase.com", "wellsfargo.com", "paypal.com", "stripe.com"
    ];

    private readonly HashSet<string> _excludedDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public IReadOnlyCollection<string> ExcludedDomains
    {
        get
        {
            lock (_lock)
            {
                return _excludedDomains.ToArray();
            }
        }
    }

    public SiteExclusionService(string? customFilePath = null)
    {
        foreach (var d in DefaultDomains)
        {
            _excludedDomains.Add(d);
        }

        if (!string.IsNullOrWhiteSpace(customFilePath) && File.Exists(customFilePath))
        {
            LoadFromFile(customFilePath);
        }
        else
        {
            TryLoadStandardLocations();
        }
    }

    private void TryLoadStandardLocations()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "defexclist.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet", "defexclist.txt")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                LoadFromFile(path);
                break;
            }
        }
    }

    public void LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            var lines = File.ReadAllLines(filePath);
            lock (_lock)
            {
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                    {
                        continue;
                    }

                    var clean = trimmed.TrimStart('*', '.');
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        _excludedDomains.Add(clean);
                    }
                }
            }
        }
        catch
        {
            // Fall back to default domains
        }
    }

    public bool IsExcluded(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost))
        {
            return false;
        }

        string host;
        if (Uri.TryCreate(urlOrHost, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
        }
        else
        {
            host = urlOrHost.Trim();
            if (host.Contains('/'))
            {
                host = host.Split('/')[0];
            }
            if (host.Contains(':'))
            {
                host = host.Split(':')[0];
            }
        }

        lock (_lock)
        {
            return _excludedDomains.Any(excluded =>
                string.Equals(host, excluded, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + excluded, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void AddExclusion(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return;
        var clean = domain.Trim().TrimStart('*', '.');
        lock (_lock)
        {
            _excludedDomains.Add(clean);
        }
    }

    public void RemoveExclusion(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return;
        var clean = domain.Trim().TrimStart('*', '.');
        lock (_lock)
        {
            _excludedDomains.Remove(clean);
        }
    }
}
