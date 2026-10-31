using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.App.ViewModels;
using SwiftGet.Engine.Probing;
using Xunit;

namespace SwiftGet.App.Tests;

public class AddUrlViewModelTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    [Theory]
    [InlineData("archive.zip", "Compressed")]
    [InlineData("backup.7z", "Compressed")]
    [InlineData("system.iso", "Compressed")]
    [InlineData("report.pdf", "Documents")]
    [InlineData("spreadsheet.xlsx", "Documents")]
    [InlineData("track01.flac", "Music")]
    [InlineData("song.mp3", "Music")]
    [InlineData("installer.msi", "Programs")]
    [InlineData("setup.exe", "Programs")]
    [InlineData("movie.mkv", "Video")]
    [InlineData("clip.mp4", "Video")]
    [InlineData("unknown_file.xyz", "General")]
    public void AutoAssignCategory_AssignsCorrectCategoryBasedOnExtension(string fileName, string expectedCategory)
    {
        // Arrange
        using var client = new HttpClient();
        var probe = new HttpProbeService(client);
        var vm = new AddUrlViewModel(probe);

        // Act
        vm.AutoAssignCategory(fileName);

        // Assert
        vm.SelectedCategory.Should().Be(expectedCategory);
    }

    [Fact]
    public void OnUrlChanged_ExtractsFileNameAndAssignsCategory()
    {
        // Arrange
        using var client = new HttpClient();
        var probe = new HttpProbeService(client);
        var vm = new AddUrlViewModel(probe);

        // Act
        vm.Url = "https://releases.ubuntu.com/24.04/ubuntu-24.04-desktop-amd64.iso";

        // Assert
        vm.FileName.Should().Be("ubuntu-24.04-desktop-amd64.iso");
        vm.SelectedCategory.Should().Be("Compressed");
    }

    [Fact]
    public async Task ProbeAsync_SuccessfulResponse_UpdatesSizeAndResume()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK);
            res.Content.Headers.ContentLength = 104_857_600; // 100 MB
            res.Headers.Add("Accept-Ranges", "bytes");
            res.Headers.Add("ETag", "\"file-etag-123\"");
            return res;
        });

        using var client = new HttpClient(handler);
        var probe = new HttpProbeService(client);
        var vm = new AddUrlViewModel(probe)
        {
            Url = "https://example.com/download/app.exe"
        };

        // Act
        await vm.ProbeAsync();

        // Assert
        vm.FileSize.Should().Be(104_857_600);
        vm.ResumeSupported.Should().BeTrue();
        vm.FormattedSize.Should().Be("100 MB");
        vm.StatusMessage.Should().Contain("100 MB");
        vm.StatusMessage.Should().Contain("Resume: Yes");
    }

    [Fact]
    public async Task ProbeAsync_WhenUserHasExplicitlyEditedFileName_PreservesUserFileName()
    {
        // Arrange: Server responds with a suggested filename via Content-Disposition
        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK);
            res.Content.Headers.ContentLength = 104_857_600;
            res.Content.Headers.TryAddWithoutValidation(
                "Content-Disposition",
                "attachment; filename=\"ServerSuggestedName.zip\"");
            return res;
        });

        using var client = new HttpClient(handler);
        var probe = new HttpProbeService(client);
        var vm = new AddUrlViewModel(probe)
        {
            Url = "https://example.com/download/file123"
        };

        // User explicitly types an authoritative file name
        vm.FileName = "UserTypedCustomName.zip";

        // Act
        await vm.ProbeAsync();

        // Assert: User edited name must be preserved
        vm.FileName.Should().Be("UserTypedCustomName.zip");
        vm.SelectedCategory.Should().Be("Compressed");
    }

    [Theory]
    [InlineData("archive.tar.gz", "Compressed")]
    [InlineData("archive.tar.bz2", "Compressed")]
    [InlineData("archive.tar.xz", "Compressed")]
    [InlineData("installer.msix", "Programs")]
    [InlineData("app.apk", "Programs")]
    [InlineData("music.opus", "Music")]
    public void AutoAssignCategory_CompoundAndModernExtensions_AssignsCorrectCategory(string fileName, string expectedCategory)
    {
        using var client = new HttpClient();
        var probe = new HttpProbeService(client);
        var vm = new AddUrlViewModel(probe);

        vm.AutoAssignCategory(fileName);
        vm.SelectedCategory.Should().Be(expectedCategory);
    }
}
