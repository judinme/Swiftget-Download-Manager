using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace SwiftGet.Engine.Security;

public sealed class TlsSecurityPolicy : ITlsSecurityPolicy
{
    private readonly HashSet<string> _pinnedThumbprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<TlsAuditLogEntry> _auditLogs = new();
    private readonly ILogger<TlsSecurityPolicy>? _logger;
    private readonly object _lock = new();

    public IReadOnlySet<string> PinnedThumbprints
    {
        get
        {
            lock (_lock)
            {
                return new HashSet<string>(_pinnedThumbprints, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public IReadOnlyList<TlsAuditLogEntry> AuditLogs => _auditLogs.ToArray();

    public TlsSecurityPolicy(ILogger<TlsSecurityPolicy>? logger = null)
    {
        _logger = logger;
    }

    public void AddPinnedThumbprint(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        var clean = thumbprint.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim();
        lock (_lock)
        {
            _pinnedThumbprints.Add(clean);
        }
    }

    public void RemovePinnedThumbprint(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        var clean = thumbprint.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim();
        lock (_lock)
        {
            _pinnedThumbprints.Remove(clean);
        }
    }

    public bool ValidateCertificate(
        HttpRequestMessage? request,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors,
        bool allowInsecureOverride)
    {
        var host = request?.RequestUri?.Host ?? "unknown";
        var subject = certificate?.Subject ?? "unknown";
        var thumbprint = certificate?.Thumbprint ?? "none";
        var errorsStr = sslPolicyErrors.ToString();

        // 1. Check pinned thumbprints first
        if (certificate != null)
        {
            lock (_lock)
            {
                if (_pinnedThumbprints.Contains(thumbprint))
                {
                    LogEntry(host, subject, thumbprint, errorsStr, true);
                    return true;
                }
            }
        }

        // 2. Check explicit user override
        if (allowInsecureOverride)
        {
            _logger?.LogWarning("TLS validation override applied for {Host}. SslPolicyErrors: {Errors}", host, errorsStr);
            LogEntry(host, subject, thumbprint, errorsStr, true);
            return true;
        }

        // 3. Standard strict validation
        var isValid = sslPolicyErrors == SslPolicyErrors.None;
        if (!isValid)
        {
            _logger?.LogError("TLS validation failed for {Host} (Subject: {Subject}, Errors: {Errors})", host, subject, errorsStr);
        }

        LogEntry(host, subject, thumbprint, errorsStr, isValid);
        return isValid;
    }

    private void LogEntry(string host, string subject, string thumbprint, string errors, bool allowed)
    {
        var entry = new TlsAuditLogEntry(host, subject, thumbprint, errors, DateTime.UtcNow, allowed);
        _auditLogs.Enqueue(entry);

        // Keep rolling log bounded to 100 entries
        while (_auditLogs.Count > 100 && _auditLogs.TryDequeue(out _))
        {
        }
    }
}
