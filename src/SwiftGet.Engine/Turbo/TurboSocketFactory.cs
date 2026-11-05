using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SwiftGet.Engine.Turbo;

/// <summary>
/// Protocol-aware socket factory and HTTP handler tuner for the Turbo Engine.
/// Configures TCP_NODELAY, dynamic BDP socket buffers, HTTP/2 multiplexing, Happy Eyeballs,
/// and RFC 1918 private LAN profiles.
/// </summary>
public static class TurboSocketFactory
{
    public const int DefaultLanBufferSize = 1024 * 1024 * 2; // 2 MB for LAN / 10 GbE
    public const int DefaultWanBufferSize = 256 * 1024;      // 256 KB for Internet

    public static bool IsPrivateNetworkHost(Uri uri)
    {
        if (uri.IsLoopback) return true;

        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;

            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                // 10.0.0.0/8
                if (bytes[0] == 10) return true;
                // 172.16.0.0/12
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                // 169.254.0.0/16 (Link Local)
                if (bytes[0] == 169 && bytes[1] == 254) return true;
            }
        }

        return false;
    }

    public static SocketsHttpHandler CreateTunableHandler(
        long estimatedBdpBytes = 0,
        IPAddress? bindLocalAddress = null,
        bool isLan = false)
    {
        var targetBufferSize = isLan
            ? DefaultLanBufferSize
            : (int)Math.Clamp(estimatedBdpBytes > 0 ? estimatedBdpBytes : DefaultWanBufferSize, 128 * 1024, 8 * 1024 * 1024);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            InitialHttp2StreamWindowSize = 4 * 1024 * 1024, // 4 MB
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true, // TCP_NODELAY: eliminate Nagle's algorithm delay
                    ReceiveBufferSize = targetBufferSize,
                    SendBufferSize = 64 * 1024
                };

                // Multi-interface aggregation: bind to specific local interface if designated
                if (bindLocalAddress != null)
                {
                    socket.Bind(new IPEndPoint(bindLocalAddress, 0));
                }

                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return handler;
    }
}
