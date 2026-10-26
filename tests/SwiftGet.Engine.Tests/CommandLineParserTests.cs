using FluentAssertions;
using SwiftGet.Engine.Cli;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class CommandLineParserTests
{
    [Fact]
    public void Parse_BareUrl_ShouldSetUrl()
    {
        var cmd = CommandLineParser.Parse(["https://example.com/test.iso"]);
        cmd.Url.Should().Be("https://example.com/test.iso");
        cmd.StartImmediately.Should().BeFalse();
        cmd.Quiet.Should().BeFalse();
    }

    [Fact]
    public void Parse_PosixFlags_ShouldExtractAllFields()
    {
        var cmd = CommandLineParser.Parse([
            "--url", "https://cdn.example.com/archive.zip",
            "--path", "C:\\Downloads",
            "--filename", "archive.zip",
            "--queue", "Nightly",
            "--start",
            "--quiet",
            "--quit"
        ]);

        cmd.Url.Should().Be("https://cdn.example.com/archive.zip");
        cmd.DestinationPath.Should().Be("C:\\Downloads");
        cmd.FileName.Should().Be("archive.zip");
        cmd.QueueName.Should().Be("Nightly");
        cmd.StartImmediately.Should().BeTrue();
        cmd.Quiet.Should().BeTrue();
        cmd.QuitAfterAdd.Should().BeTrue();
    }

    [Fact]
    public void Parse_IdmSwitches_ShouldExtractAllFields()
    {
        var cmd = CommandLineParser.Parse([
            "/d", "https://cdn.example.com/setup.exe",
            "/p", "C:\\Installs",
            "/f", "setup.exe",
            "/q",
            "/s",
            "/n",
            "/a"
        ]);

        cmd.Url.Should().Be("https://cdn.example.com/setup.exe");
        cmd.DestinationPath.Should().Be("C:\\Installs");
        cmd.FileName.Should().Be("setup.exe");
        cmd.QueueName.Should().Be("default");
        cmd.StartImmediately.Should().BeTrue();
        cmd.Quiet.Should().BeTrue();
        cmd.QuitAfterAdd.Should().BeTrue();
        cmd.QuitAfterCompletion.Should().BeTrue();
    }

    [Fact]
    public void Parse_IdmHangUpSwitch_ShouldSetHangUpAfterCompletion()
    {
        var cmd = CommandLineParser.Parse(["/d", "https://example.com/file.zip", "/h"]);
        cmd.HangUpAfterCompletion.Should().BeTrue();
        cmd.Url.Should().Be("https://example.com/file.zip");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    [InlineData("/help")]
    public void Parse_HelpFlags_ShouldSetShowHelp(string flag)
    {
        var cmd = CommandLineParser.Parse([flag]);
        cmd.ShowHelp.Should().BeTrue();
    }

    [Fact]
    public void Parse_EmptyOrNullArgs_ShouldReturnDefaultCommand()
    {
        var cmd1 = CommandLineParser.Parse(null);
        cmd1.HasActionableArguments.Should().BeFalse();

        var cmd2 = CommandLineParser.Parse([]);
        cmd2.HasActionableArguments.Should().BeFalse();
    }

    [Fact]
    public void Parse_BareMagnetUri_ShouldDetectTorrentAndSetUrl()
    {
        var magnet = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Ubuntu";
        var cmd = CommandLineParser.Parse([magnet]);

        cmd.Url.Should().Be(magnet);
        cmd.IsTorrent.Should().BeTrue();
    }

    [Fact]
    public void Parse_BareTorrentFile_ShouldDetectTorrentAndSetUrl()
    {
        var torrentPath = "C:\\Downloads\\archlinux.torrent";
        var cmd = CommandLineParser.Parse([torrentPath]);

        cmd.Url.Should().Be(torrentPath);
        cmd.IsTorrent.Should().BeTrue();
    }

    [Fact]
    public void Parse_TorrentFlag_ShouldExtractTorrentAndDestination()
    {
        var cmd = CommandLineParser.Parse([
            "--torrent", "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709",
            "--path", "D:\\Torrents",
            "--start"
        ]);

        cmd.Url.Should().Be("magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709");
        cmd.DestinationPath.Should().Be("D:\\Torrents");
        cmd.StartImmediately.Should().BeTrue();
        cmd.IsTorrent.Should().BeTrue();
    }
}
