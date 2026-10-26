using System;
using System.IO;
using FluentAssertions;
using SwiftGet.Engine.Security;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class SiteExclusionServiceTests
{
    [Theory]
    [InlineData("https://www.google.com/search?q=test", true)]
    [InlineData("https://accounts.google.com/signin", true)]
    [InlineData("https://netflix.com/browse", true)]
    [InlineData("https://subdomain.bankofamerica.com/login", true)]
    [InlineData("https://cdn.speedtest.net/file.bin", false)]
    [InlineData("https://releases.ubuntu.com/22.04/ubuntu.iso", false)]
    public void IsExcluded_DefaultExclusions_ShouldClassifyCorrectly(string url, bool expectedExcluded)
    {
        var service = new SiteExclusionService();
        service.IsExcluded(url).Should().Be(expectedExcluded);
    }

    [Fact]
    public void AddAndRemoveExclusion_ShouldUpdateDynamically()
    {
        var service = new SiteExclusionService();
        service.IsExcluded("https://mycustomsite.org/file").Should().BeFalse();

        service.AddExclusion("mycustomsite.org");
        service.IsExcluded("https://mycustomsite.org/file").Should().BeTrue();
        service.IsExcluded("https://sub.mycustomsite.org/file").Should().BeTrue();

        service.RemoveExclusion("mycustomsite.org");
        service.IsExcluded("https://mycustomsite.org/file").Should().BeFalse();
    }

    [Fact]
    public void LoadFromFile_CustomDefexclist_ShouldParseCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tempFile,
            [
                "# Comment line",
                "*.example-corp.com",
                "custom-tracker.net",
                "",
                "; Semicolon comment"
            ]);

            var service = new SiteExclusionService(tempFile);
            service.IsExcluded("https://api.example-corp.com/v1").Should().BeTrue();
            service.IsExcluded("https://custom-tracker.net/announce").Should().BeTrue();
            service.IsExcluded("https://other.com/file").Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
