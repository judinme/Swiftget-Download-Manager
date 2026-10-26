using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using SwiftGet.Engine.Security;

namespace SwiftGet.Engine.Network;

public record HttpClientConfiguration
{
    public bool EnableHttp2 { get; init; } = true;
    public string? ProxyAddress { get; init; }
    public NetworkCredential? Credentials { get; init; }
    public bool AllowInsecureCertificates { get; init; }
    public ITlsSecurityPolicy? SecurityPolicy { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public static class HttpClientProvider
{
    public static HttpClient CreateClient(HttpClientConfiguration? config = null)
    {
        config ??= new HttpClientConfiguration();

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = config.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = config.EnableHttp2
        };

        // Proxy configuration
        if (!string.IsNullOrWhiteSpace(config.ProxyAddress))
        {
            handler.Proxy = new WebProxy(config.ProxyAddress);
            handler.UseProxy = true;
        }

        // Credentials for Basic, Digest, NTLM
        if (config.Credentials != null)
        {
            handler.Credentials = config.Credentials;
            handler.PreAuthenticate = true;
        }

        // TLS Validation Policy: strictly validated by default, with optional ITlsSecurityPolicy
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
            {
                var cert2 = certificate as X509Certificate2 ?? (certificate != null ? new X509Certificate2(certificate) : null);
                var req = sender as HttpRequestMessage;

                if (config.SecurityPolicy != null)
                {
                    return config.SecurityPolicy.ValidateCertificate(req, cert2, chain, sslPolicyErrors, config.AllowInsecureCertificates);
                }

                if (config.AllowInsecureCertificates)
                {
                    return true;
                }
                return sslPolicyErrors == SslPolicyErrors.None;
            }
        };

        return new HttpClient(handler, disposeHandler: true);
    }
}
