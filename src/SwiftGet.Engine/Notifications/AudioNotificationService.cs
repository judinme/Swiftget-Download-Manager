using System.IO;
using System.Runtime.InteropServices;

namespace SwiftGet.Engine.Notifications;

/// <summary>
/// Default implementation of audio notifications using Windows multimedia API and system beeps.
/// </summary>
public sealed class AudioNotificationService : IAudioNotificationService
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndFilename = 0x00020000;

    private const uint MbOk = 0x00000000;
    private const uint MbIconHand = 0x00000010;
    private const uint MbIconExclamation = 0x00000030;
    private const uint MbIconAsterisk = 0x00000040;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);

    public void PlaySound(SoundNotificationType type, string? customWaveFilePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(customWaveFilePath) && File.Exists(customWaveFilePath))
        {
            try
            {
                PlaySound(customWaveFilePath, IntPtr.Zero, SndAsync | SndFilename | SndNoDefault);
                return;
            }
            catch
            {
                // Fall back to system sound
            }
        }

        var soundType = type switch
        {
            SoundNotificationType.DownloadComplete => MbIconAsterisk,
            SoundNotificationType.DownloadError => MbIconHand,
            SoundNotificationType.QueueComplete => MbIconExclamation,
            _ => MbOk
        };

        try
        {
            MessageBeep(soundType);
        }
        catch
        {
            // Ignore sound dispatch failure
        }
    }
}
