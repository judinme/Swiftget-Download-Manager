using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace SwiftGet.Engine.Security;

public sealed record TlsAuditLogEntry(
    string Hostname,
    string CertificateSubject,
    string CertificateThumbprint,
    string SslPolicyErrors,
    DateTime TimestampUtc,
    bool WasAllowed
);

public interface ITlsSecurityPolicy
{
    bool ValidateCertificate(
        HttpRequestMessage? request,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors,
        bool allowInsecureOverride);

    void AddPinnedThumbprint(string thumbprint);
    void RemovePinnedThumbprint(string thumbprint);
    IReadOnlySet<string> PinnedThumbprints { get; }
    IReadOnlyList<TlsAuditLogEntry> AuditLogs { get; }
}
