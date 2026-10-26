using FluentAssertions;
using SwiftGet.Engine.Notifications;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class AudioNotificationServiceTests
{
    [Fact]
    public void PlaySound_WithStandardEvents_ShouldNotThrow()
    {
        var service = new AudioNotificationService();

        var act1 = () => service.PlaySound(SoundNotificationType.DownloadComplete);
        var act2 = () => service.PlaySound(SoundNotificationType.DownloadError);
        var act3 = () => service.PlaySound(SoundNotificationType.QueueComplete);

        act1.Should().NotThrow();
        act2.Should().NotThrow();
        act3.Should().NotThrow();
    }

    [Fact]
    public void PlaySound_WithNonExistentCustomPath_ShouldFallbackGracefullyWithoutThrowing()
    {
        var service = new AudioNotificationService();

        var act = () => service.PlaySound(SoundNotificationType.DownloadComplete, "C:\\non_existent_folder\\sound.wav");

        act.Should().NotThrow();
    }
}
