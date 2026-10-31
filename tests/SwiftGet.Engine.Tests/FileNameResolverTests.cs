using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.IO;
using SwiftGet.Engine.Probing;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class FileNameResolverTests
{
    private readonly FileNameResolver _resolver = new();

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData("attachment; filename=\"a b [x].zip\"", "a b [x].zip")]
    [InlineData("attachment; filename='single quoted name.pdf'", "single quoted name.pdf")]
    [InlineData("attachment; filename=unquoted_file.tar.gz", "unquoted_file.tar.gz")]
    [InlineData("attachment; filename=\"malformed\"quotes.zip\"", "malformedquotes.zip")]
    [InlineData("inline; filename=\"document.docx\"", "document.docx")]
    public void Resolve_StandardContentDisposition_ParsesFileName(string cdHeader, string expected)
    {
        var context = new FileNameResolutionContext
        {
            OriginalUrl = "https://example.com/download",
            ContentDisposition = cdHeader
        };

        var result = _resolver.Resolve(context);
        result.FileName.Should().Be(expected);
        result.Source.Should().Be(FileNameSource.ContentDisposition);
    }

    [Theory]
    // Tamil: தமிழ்.pdf
    [InlineData("attachment; filename*=UTF-8''%E0%AE%A4%E0%AE%AE%E0%AE%BF%E0%AE%B4%E0%AF%8D.pdf", "தமிழ்.pdf")]
    // Arabic: ملف.pdf
    [InlineData("attachment; filename*=UTF-8''%D9%85%D9%84%D9%81.pdf", "ملف.pdf")]
    // Japanese: テスト.zip
    [InlineData("attachment; filename*=UTF-8''%E3%83%86%E3%82%B9%E3%83%88.zip", "テスト.zip")]
    // Mixed: File (ملف) [100%].zip
    [InlineData("attachment; filename*=UTF-8''File%20%28%D9%85%D9%84%D9%81%29%20%5B100%25%5D.zip", "File (ملف) [100%].zip")]
    public void Resolve_Rfc5987UnicodeContentDisposition_ParsesCorrectly(string cdHeader, string expected)
    {
        var context = new FileNameResolutionContext
        {
            OriginalUrl = "https://example.com/file",
            ContentDisposition = cdHeader
        };

        var result = _resolver.Resolve(context);
        result.FileName.Should().Be(expected);
        result.Source.Should().Be(FileNameSource.ContentDisposition);
    }

    [Fact]
    public void Resolve_WhenBothFilenameAndFilenameStarPresent_PrefersFilenameStar()
    {
        var context = new FileNameResolutionContext
        {
            OriginalUrl = "https://example.com/file",
            ContentDisposition = "attachment; filename=\"fallback.pdf\"; filename*=UTF-8''%D9%85%D9%84%D9%81.pdf"
        };

        var result = _resolver.Resolve(context);
        result.FileName.Should().Be("ملف.pdf");
    }

    [Fact]
    public void Resolve_ExactIssueReproducer_FileCrTokenUrlWithContentDisposition_DoesNotUseExtensionlessToken()
    {
        // Exact case from the user's bug report:
        // Original: https://s1-filecr.xyz/d08df4022443f114?do=...&token=...
        // Header: Content-Disposition: attachment; filename="Wondershare UniConverter 18.0.1.66 (x64) Multilingual [FileCR].zip"
        var context = new FileNameResolutionContext
        {
            OriginalUrl = "https://s1-filecr.xyz/d08df4022443f114?do=download&token=abcdef123456",
            FinalUrl = "https://s1-filecr.xyz/d08df4022443f114?do=download&token=abcdef123456",
            ContentDisposition = "attachment; filename=\"Wondershare UniConverter 18.0.1.66 (x64) Multilingual [FileCR].zip\"",
            ContentType = "application/zip",
            BrowserCapturedName = "d08df4022443f114" // Chrome's onCreated initially saw only the token slug
        };

        var result = _resolver.Resolve(context);
        result.FileName.Should().Be("Wondershare UniConverter 18.0.1.66 (x64) Multilingual [FileCR].zip");
        result.Extension.Should().Be(".zip");
        result.Source.Should().Be(FileNameSource.ContentDisposition);
    }

    [Fact]
    public void Resolve_UserEditedName_IsAuthoritativeAndNeverOverwritten()
    {
        var context = new FileNameResolutionContext
        {
            OriginalUrl = "https://s1-filecr.xyz/d08df4022443f114?token=123",
            UserEditedName = "MyAuthoritativeCustomName.zip",
            UserExplicitlyNamed = true,
            ContentDisposition = "attachment; filename=\"ServerSuggestedName.zip\"",
            ContentType = "application/zip"
        };

        var result = _resolver.Resolve(context);
        result.FileName.Should().Be("MyAuthoritativeCustomName.zip");
        result.Source.Should().Be(FileNameSource.UserEdited);
    }

    [Fact]
    public void Resolve_ExtensionlessUrlWithContentTypeZip_RecoversZipExtension()
    {
        var context = new FileNameResolutionContext
        {
            OriginalUrl = "https://example.com/assets/d08df4022443f114",
            ContentType = "application/zip"
        };

        var result = _resolver.Resolve(context);
        result.FileName.Should().Be("d08df4022443f114.zip");
        result.Extension.Should().Be(".zip");
        result.ExtensionRecovered.Should().BeTrue();
    }

    [Fact]
    public void Resolve_ExtensionlessUrlWithOctetStreamAndMagicBytes_RecoversExtension()
    {
        // PK\x03\x04 zip signature
        var zipBytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 };

        var context = new FileNameResolutionContext
        {
            OriginalUrl = "https://example.com/assets/d08df4022443f114",
            ContentType = "application/octet-stream",
            InitialBytes = zipBytes
        };

        var result = _resolver.Resolve(context);
        result.FileName.Should().Be("d08df4022443f114.zip");
        result.ExtensionRecovered.Should().BeTrue();
    }

    [Theory]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, ".rar")]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, ".7z")]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, ".exe")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, ".pdf")]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 }, ".gz")]
    [InlineData(new byte[] { 0x42, 0x5A, 0x68 }, ".bz2")]
    [InlineData(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, ".xz")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    public void RecoverExtension_FromMagicBytes_SniffsCorrectly(byte[] magic, string expectedExt)
    {
        var recovered = _resolver.RecoverExtension("downloaded_file", "application/octet-stream", magic);
        recovered.Should().Be(expectedExt);
    }

    [Fact]
    public void Resolve_RedirectChain_PrefersFinalUrlWhenOriginalIsGeneric()
    {
        var context = new FileNameResolutionContext
        {
            OriginalUrl = "https://example.com/download.php?id=999",
            FinalUrl = "https://cdn.example.com/releases/v2.5/SwiftGet-Setup.exe"
        };

        var result = _resolver.Resolve(context);
        result.FileName.Should().Be("SwiftGet-Setup.exe");
        result.Source.Should().Be(FileNameSource.FinalUrlPath);
    }

    [Theory]
    [InlineData("https://cdn.example.com/get?filename=package.tar.gz&sig=abc", "package.tar.gz")]
    [InlineData("https://s3.amazonaws.com/bucket/key?response-content-disposition=attachment%3B%20filename%3D%22archive.7z%22", "archive.7z")]
    [InlineData("https://example.com/dl?file=setup.msi", "setup.msi")]
    public void Resolve_QueryStringHints_ExtractedWhenPathIsGeneric(string url, string expected)
    {
        var context = new FileNameResolutionContext
        {
            OriginalUrl = url
        };

        var result = _resolver.Resolve(context);
        result.FileName.Should().Be(expected);
    }

    [Theory]
    [InlineData("archive.tar.gz", ".tar.gz")]
    [InlineData("archive.tar.bz2", ".tar.bz2")]
    [InlineData("archive.tar.xz", ".tar.xz")]
    [InlineData("archive.tar.zst", ".tar.zst")]
    [InlineData("script.user.js", ".user.js")]
    [InlineData("normal.zip", ".zip")]
    [InlineData("noext", "")]
    public void GetCompoundExtension_PreservesCompoundExtensions(string fileName, string expected)
    {
        var ext = _resolver.GetCompoundExtension(fileName);
        ext.Should().Be(expected);
    }

    [Theory]
    [InlineData("../../windows/system32/cmd.exe", "____windows_system32_cmd.exe")]
    [InlineData("..\\..\\boot.ini", "____boot.ini")]
    [InlineData("CON.zip", "_CON.zip")]
    [InlineData("PRN", "_PRN")]
    [InlineData("AUX.tar.gz", "_AUX.tar.gz")]
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("COM1.bin", "_COM1.bin")]
    [InlineData("LPT2.dat", "_LPT2.dat")]
    [InlineData("name?with*invalid<chars>.zip", "namewithinvalidchars.zip")]
    [InlineData("spaced file name.zip...", "spaced file name.zip")]
    public void SanitizeFileName_CleansDangerousAndReservedWindowsNames(string rawName, string expected)
    {
        var sanitized = _resolver.SanitizeFileName(rawName);
        sanitized.Should().Be(expected);
    }

    [Fact]
    public void SanitizeFileName_ShortensOverLongNames_WhilePreservingExtension()
    {
        var veryLongBase = new string('A', 300);
        var longFileName = veryLongBase + ".tar.gz";

        var sanitized = _resolver.SanitizeFileName(longFileName);
        sanitized.Length.Should().BeLessThanOrEqualTo(245);
        sanitized.Should().EndWith(".tar.gz");
    }

    [Fact]
    public void ResolveUniquePath_HandlesCollisionsLikeIdm()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SwiftGet_Collision_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var firstFile = Path.Combine(tempDir, "document.pdf");
            File.WriteAllText(firstFile, "test1");

            var unique1 = _resolver.ResolveUniquePath(tempDir, "document.pdf");
            Path.GetFileName(unique1).Should().Be("document (1).pdf");
            File.WriteAllText(unique1, "test2");

            var unique2 = _resolver.ResolveUniquePath(tempDir, "document.pdf");
            Path.GetFileName(unique2).Should().Be("document (2).pdf");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData(".exe", true)]
    [InlineData(".msi", true)]
    [InlineData(".bat", true)]
    [InlineData(".cmd", true)]
    [InlineData(".ps1", true)]
    [InlineData(".vbs", true)]
    [InlineData(".js", true)]
    [InlineData(".scr", true)]
    [InlineData(".zip", false)]
    [InlineData(".pdf", false)]
    [InlineData(".mp4", false)]
    public void IsDangerousExtension_FlagsHarmfulExtensions(string ext, bool expectedDangerous)
    {
        _resolver.IsDangerousExtension(ext).Should().Be(expectedDangerous);
    }

    [Theory]
    [InlineData("archive.zip", "Compressed")]
    [InlineData("backup.tar.gz", "Compressed")]
    [InlineData("video.mp4", "Video")]
    [InlineData("song.flac", "Music")]
    [InlineData("installer.msi", "Programs")]
    [InlineData("photo.png", "Images")]
    [InlineData("document.pdf", "Documents")]
    [InlineData("linux.iso", "Compressed")]
    [InlineData("unknown_file", "General")]
    public void ResolveCategory_MapsExtensionsToCategoriesCorrectly(string fileName, string expectedCategory)
    {
        var category = FileNameResolver.ResolveCategory(fileName);
        category.Should().Be(expectedCategory);
    }

    [Fact]
    public async Task HttpProbeService_WithFallbackAndContentDisposition_ExtractsCorrectName()
    {
        // Test integration where HEAD returns 405 Method Not Allowed, falling back to GET Range 0-511,
        // which sends Content-Disposition with the real .zip file name
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.Method == HttpMethod.Head)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            }

            if (req.Method == HttpMethod.Get && req.Headers.Range != null)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.PartialContent);
                var zipHeader = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00 };
                resp.Content = new ByteArrayContent(zipHeader);
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 7, 425365127);
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                resp.Content.Headers.TryAddWithoutValidation(
                    "Content-Disposition",
                    "attachment; filename=\"Wondershare UniConverter 18.0.1.66 (x64) Multilingual [FileCR].zip\"");
                return Task.FromResult(resp);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        });

        using var client = new HttpClient(handler);
        var probe = new HttpProbeService(client, _resolver);
        var probeResult = await probe.ProbeAsync("https://s1-filecr.xyz/d08df4022443f114?do=download&token=test");

        probeResult.IsSuccess.Should().BeTrue();
        probeResult.SuggestedFileName.Should().Be("Wondershare UniConverter 18.0.1.66 (x64) Multilingual [FileCR].zip");
        probeResult.ContentType.Should().Be("application/zip");
    }

    [Fact]
    public void ArchitecturalGuard_NoUnauthorizedUrlPathSegmentNamingOutsideResolver()
    {
        // Scans all .cs files in src/ to guarantee no direct Path.GetFileName(uri.AbsolutePath)
        // or Path.GetFileName(uri.LocalPath) calls remain outside FileNameResolver.cs
        var srcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src"));
        if (!Directory.Exists(srcDir))
        {
            // Fallback for different build output directory depths
            srcDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src"));
            if (!Directory.Exists(srcDir))
            {
                var current = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (current != null && !Directory.Exists(Path.Combine(current.FullName, "src")))
                {
                    current = current.Parent;
                }
                if (current != null) srcDir = Path.Combine(current.FullName, "src");
            }
        }

        Directory.Exists(srcDir).Should().BeTrue($"src directory must exist at {srcDir}");

        var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);
        var violations = new System.Collections.Generic.List<string>();

        foreach (var file in csFiles)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("FileNameResolver.cs", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("PathResolver.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Contains("Path.GetFileName(uri.AbsolutePath)") ||
                    line.Contains("Path.GetFileName(uri.LocalPath)"))
                {
                    violations.Add($"{Path.GetRelativePath(srcDir, file)}:{i + 1} -> {line.Trim()}");
                }
            }
        }

        violations.Should().BeEmpty(
            "Path.GetFileName(uri.AbsolutePath/LocalPath) must NOT be used outside FileNameResolver. " +
            "All candidate file name resolutions must go through IFileNameResolver. " +
            $"Violations found:\n{string.Join("\n", violations)}");
    }
}
