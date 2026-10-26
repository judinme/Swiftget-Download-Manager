using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core.Models;

namespace SwiftGet.Engine.Automation;

public interface IAfterCompletionService
{
    event EventHandler? ExitAppRequested;
    Task<bool> ExecuteActionAsync(
        AfterCompletionAction action,
        string? customCommand = null,
        int shutdownCountdownSeconds = 30,
        CancellationToken ct = default);
    bool AbortShutdown();
}

public class AfterCompletionService : IAfterCompletionService
{
    private readonly ILogger<AfterCompletionService> _logger;

    public event EventHandler? ExitAppRequested;

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public AfterCompletionService(ILogger<AfterCompletionService>? logger = null)
    {
        _logger = logger ?? NullLogger<AfterCompletionService>.Instance;
    }

    public Func<AfterCompletionAction, int, CancellationToken, Task<bool>>? ConfirmActionAsync { get; set; }

    public async Task<bool> ExecuteActionAsync(
        AfterCompletionAction action,
        string? customCommand = null,
        int shutdownCountdownSeconds = 30,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Executing after-completion action: {Action}", action);

        // Security requirement: Action confirmation gate for machine state changes and external command execution
        if (action is AfterCompletionAction.Shutdown or AfterCompletionAction.Hibernate or AfterCompletionAction.Sleep or AfterCompletionAction.ExecuteCommand)
        {
            if (ConfirmActionAsync != null)
            {
                var confirmed = await ConfirmActionAsync(action, Math.Max(30, shutdownCountdownSeconds), ct).ConfigureAwait(false);
                if (!confirmed)
                {
                    _logger.LogInformation("Action {Action} was declined or aborted by user confirmation.", action);
                    return false;
                }
            }
        }

        switch (action)
        {
            case AfterCompletionAction.None:
                return true;

            case AfterCompletionAction.ExitApp:
                ExitAppRequested?.Invoke(this, EventArgs.Empty);
                return true;

            case AfterCompletionAction.Sleep:
                return TriggerSleep();

            case AfterCompletionAction.Hibernate:
                return TriggerHibernate();

            case AfterCompletionAction.Shutdown:
                return TriggerShutdown(Math.Max(30, shutdownCountdownSeconds));

            case AfterCompletionAction.ExecuteCommand:
                return await TriggerExecuteCommandAsync(customCommand, ct).ConfigureAwait(false);

            default:
                _logger.LogWarning("Unknown AfterCompletionAction: {Action}", action);
                return false;
        }
    }

    private bool TriggerSleep()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // Sleep: hibernate = false
                return SetSuspendState(false, false, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to invoke SetSuspendState for sleep");
            }
        }
        return false;
    }

    private bool TriggerHibernate()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/h",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                return p != null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute shutdown.exe /h for hibernate");
            }
        }
        return false;
    }

    private bool TriggerShutdown(int countdownSeconds)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = $"/s /t {Math.Max(0, countdownSeconds)} /c \"SwiftGet completed all downloads in queue.\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                return p != null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute shutdown.exe /s");
            }
        }
        return false;
    }

    public bool AbortShutdown()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/a",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                return p?.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute shutdown.exe /a");
            }
        }
        return false;
    }

    private async Task<bool> TriggerExecuteCommandAsync(string? customCommand, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customCommand))
        {
            _logger.LogWarning("ExecuteCommand requested but command string is empty.");
            return false;
        }

        if (customCommand.Contains('&') || customCommand.Contains('|') || customCommand.Contains(';') ||
            customCommand.Contains('>') || customCommand.Contains('<') || customCommand.Contains('`'))
        {
            _logger.LogWarning("Blocked unsafe custom action command containing shell meta-characters: {Cmd}", customCommand);
            return false;
        }

        try
        {
            var trimmed = customCommand.Trim();
            string fileName;
            string arguments = string.Empty;

            if (trimmed.StartsWith('\"'))
            {
                var endQuote = trimmed.IndexOf('\"', 1);
                if (endQuote > 1)
                {
                    fileName = trimmed[1..endQuote];
                    arguments = trimmed[(endQuote + 1)..].Trim();
                }
                else
                {
                    fileName = trimmed.Trim('\"');
                }
            }
            else
            {
                var spaceIndex = trimmed.IndexOf(' ');
                if (spaceIndex > 0)
                {
                    fileName = trimmed[..spaceIndex];
                    arguments = trimmed[(spaceIndex + 1)..].Trim();
                }
                else
                {
                    fileName = trimmed;
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute custom command: {Cmd}", customCommand);
            return false;
        }
    }
}
