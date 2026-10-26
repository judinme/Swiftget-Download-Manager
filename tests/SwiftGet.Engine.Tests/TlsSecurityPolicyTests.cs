using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using SwiftGet.Engine.Security;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class TlsSecurityPolicyTests : IDisposable
{
    private readonly X509Certificate2 _testCert;

    public TlsSecurityPolicyTests()
    {
        // Generate an in-memory self-signed certificate for unit testing
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=swiftget.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _testCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
    }

    public void Dispose()
    {
        _testCert.Dispose();
    }

    [Fact]
    public void ValidateCertificate_StrictValidation_RejectsUntrustedCertificates()
    {
        var policy = new TlsSecurityPolicy();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://untrusted-site.com");

        var isValid = policy.ValidateCertificate(
            req,
            _testCert,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            allowInsecureOverride: false);

        isValid.Should().BeFalse();
        policy.AuditLogs.Should().HaveCount(1);
        policy.AuditLogs[0].WasAllowed.Should().BeFalse();
        policy.AuditLogs[0].Hostname.Should().Be("untrusted-site.com");
    }

    [Fact]
    public void ValidateCertificate_PinnedThumbprint_AllowsUntrustedCertificate()
    {
        var policy = new TlsSecurityPolicy();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://enterprise-internal.corp");

        policy.AddPinnedThumbprint(_testCert.Thumbprint);

        var isValid = policy.ValidateCertificate(
            req,
            _testCert,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            allowInsecureOverride: false);

        isValid.Should().BeTrue();
        policy.AuditLogs.Should().HaveCount(1);
        policy.AuditLogs[0].WasAllowed.Should().BeTrue();
        policy.AuditLogs[0].CertificateThumbprint.Should().Be(_testCert.Thumbprint);
    }

    [Fact]
    public void ValidateCertificate_AllowInsecureOverride_PermitsConnectionAndAudits()
    {
        var policy = new TlsSecurityPolicy();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://self-signed-dev.local");

        var isValid = policy.ValidateCertificate(
            req,
            _testCert,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            allowInsecureOverride: true);

        isValid.Should().BeTrue();
        policy.AuditLogs.Should().HaveCount(1);
        policy.AuditLogs[0].WasAllowed.Should().BeTrue();
    }

    [Fact]
    public void AddAndRemovePinnedThumbprints_MaintainsCleanSet()
    {
        var policy = new TlsSecurityPolicy();
        policy.AddPinnedThumbprint("AA:BB:CC:DD");

        policy.PinnedThumbprints.Should().Contain("AABBCCDD");

        policy.RemovePinnedThumbprint("AA:BB:CC:DD");
        policy.PinnedThumbprints.Should().NotContain("AABBCCDD");
    }
}
