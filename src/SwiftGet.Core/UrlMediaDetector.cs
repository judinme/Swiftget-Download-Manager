using System;

namespace SwiftGet.Core;

public static class UrlMediaDetector
{
    public static bool IsStreamingMediaPlatformUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.ToLowerInvariant();
        return host.Contains("youtube.com") ||
               host.Contains("youtu.be") ||
               host.Contains("vimeo.com") ||
               host.Contains("dailymotion.com") ||
               host.Contains("tiktok.com") ||
               host.Contains("twitch.tv") ||
               host.Contains("bilibili.com") ||
               host.Contains("soundcloud.com") ||
               (host.Contains("facebook.com") && uri.AbsolutePath.Contains("/videos/")) ||
               (host.Contains("instagram.com") && (uri.AbsolutePath.Contains("/reel/") || uri.AbsolutePath.Contains("/p/")));
    }
}
