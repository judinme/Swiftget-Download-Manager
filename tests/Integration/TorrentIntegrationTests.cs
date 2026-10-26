using System.Net;
using FluentAssertions;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.Integration;

public class TorrentIntegrationTests
{
    [Theory]
    [InlineData("../../etc/shadow", "etc\\shadow")]
    [InlineData("..\\..\\windows\\system32\\calc.exe", "windows\\system32\\calc.exe")]
    [InlineData("data/./sub/../file.txt", "data\\file.txt")]
    [InlineData("payload.exe:zone.identifier", "payload.exe")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("AUX.dat", "_AUX.dat")]
    [InlineData("", "unnamed_file.bin")]
    public void TorrentPathSanitizer_SanitizesRelativePaths(string input, string expected)
    {
        var sanitized = TorrentPathSanitizer.SanitizeRelativePath(input);
        sanitized.Should().Be(expected.Replace('\\', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void TorrentPathSanitizer_GetSafeFullPath_PreventsDirectoryEscape()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "swiftget_torrent_safe_base");
        Directory.CreateDirectory(baseDir);

        try
        {
            var safePath = TorrentPathSanitizer.GetSafeFullPath(baseDir, "subfolder/movie.mkv");
            safePath.Should().StartWith(baseDir);

            // Traversal attempt should be sanitized to stay inside baseDir
            var traversalPath = TorrentPathSanitizer.GetSafeFullPath(baseDir, "../../../../escaped.exe");
            traversalPath.Should().StartWith(baseDir);
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
        }
    }

    [Theory]
    [InlineData("http://tracker.opentrackr.org:1337/announce", false, true)]
    [InlineData("udp://tracker.openbittorrent.com:6969/announce", false, true)]
    [InlineData("https://tracker.tamersunion.org:443/announce", false, true)]
    [InlineData("ftp://malicious.org/announce", false, false)]
    [InlineData("http://127.0.0.1:6969/announce", false, false)]
    [InlineData("http://127.0.0.1:6969/announce", true, true)]
    [InlineData("http://10.0.0.1:6969/announce", false, false)]
    [InlineData("http://192.168.1.1:6969/announce", false, false)]
    [InlineData("http://localhost:6969/announce", false, false)]
    [InlineData("http://localhost:6969/announce", true, true)]
    public void TrackerSafetyValidator_EvaluatesTrackerSecurity(string url, bool allowLocal, bool expectedSafe)
    {
        var isSafe = TrackerSafetyValidator.IsSafeTrackerUrl(url, allowLocal, out var reason);
        isSafe.Should().Be(expectedSafe, $"reason: {reason}");
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.5.4", true)]
    [InlineData("192.168.0.100", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    public void TrackerSafetyValidator_IdentifiesPrivateOrLoopbackIps(string ipStr, bool expectedPrivate)
    {
        var ip = IPAddress.Parse(ipStr);
        TrackerSafetyValidator.IsPrivateOrLoopback(ip).Should().Be(expectedPrivate);
    }
}
