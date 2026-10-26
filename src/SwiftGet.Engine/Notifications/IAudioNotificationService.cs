namespace SwiftGet.Engine.Notifications;

/// <summary>
/// Service contract for playing audio notifications on download lifecycle events.
/// </summary>
public interface IAudioNotificationService
{
    /// <summary>
    /// Plays an audio notification corresponding to the event type.
    /// </summary>
    /// <param name="type">The sound notification type.</param>
    /// <param name="customWaveFilePath">Optional custom WAV audio file path.</param>
    void PlaySound(SoundNotificationType type, string? customWaveFilePath = null);
}
