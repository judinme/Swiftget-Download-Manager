using System;
using System.Net;
using System.Net.Sockets;

namespace SwiftGet.Engine.Torrent;

public static class TrackerSafetyValidator
{
    public static bool IsSafeTrackerUrl(string announceUrl, bool allowLocalTrackers, out string? reason)
    {
        reason = null;

        if (string.IsNullOrWhiteSpace(announceUrl))
        {
            reason = "Announce URL is empty.";
            return false;
        }

        if (!Uri.TryCreate(announceUrl, UriKind.Absolute, out var uri))
        {
            reason = $"Malformed announce URL: '{announceUrl}'";
            return false;
        }

        // BitTorrent trackers use http, https, or udp schemes
        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "udp", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Unsupported tracker scheme: '{uri.Scheme}'";
            return false;
        }

        var host = uri.DnsSafeHost;

        // Check if host is direct IP address
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IsPrivateOrLoopback(ip) && !allowLocalTrackers)
            {
                reason = $"Tracker IP '{ip}' is in a private or loopback range, which is disallowed for SSRF protection.";
                return false;
            }
            return true;
        }

        // Hostname check
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowLocalTrackers)
            {
                reason = $"Tracker host '{host}' is a local or internal domain, which is disallowed for SSRF protection.";
                return false;
            }
            return true;
        }

        return true;
    }

    public static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.IPv6Loopback) || ip.Equals(IPAddress.IPv6Any);
        }

        byte[] bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;

        // 0.0.0.0/8
        if (bytes[0] == 0) return true;

        // 10.0.0.0/8
        if (bytes[0] == 10) return true;

        // 127.0.0.0/8
        if (bytes[0] == 127) return true;

        // 169.254.0.0/16 (Link-local)
        if (bytes[0] == 169 && bytes[1] == 254) return true;

        // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;

        // 224.0.0.0/4 (Multicast) or 255.255.255.255 (Broadcast)
        if (bytes[0] >= 224) return true;

        return false;
    }
}
