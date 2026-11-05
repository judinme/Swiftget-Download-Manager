using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace SwiftGet.App.Services;

/// <summary>
/// Manages OS execution priority, Windows 11 EcoQoS/Power Throttling opt-out,
/// and sleep prevention during active download transfers.
/// </summary>
public sealed class ProcessPriorityService
{
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_AWAYMODE_REQUIRED = 0x00000040;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        int processInformationSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private int _activeTransfersCount;
    private readonly object _sync = new();

    public bool IsThrottlingDisabled { get; private set; }
    public ProcessPriorityClass CurrentPriorityClass => Process.GetCurrentProcess().PriorityClass;

    public ProcessPriorityService()
    {
        DisablePowerThrottling();
    }

    /// <summary>
    /// Disables Windows 11 EcoQoS (Execution Speed Power Throttling) for the SwiftGet process.
    /// Ensures downloads running while minimized or in system tray receive full CPU slice.
    /// </summary>
    public void DisablePowerThrottling()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0 // 0 = Opt-out of EcoQoS execution speed throttling
            };

            int size = Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
            bool success = SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling, ref state, size);
            if (success)
            {
                IsThrottlingDisabled = true;
                Log.Information("[ProcessPriority] Successfully opted out of Windows EcoQoS power throttling.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ProcessPriority] Failed to configure EcoQoS power throttling state.");
        }
    }

    /// <summary>
    /// Notifies the priority service of an active download state transition.
    /// Elevates process priority to AboveNormal and prevents system sleep while transfers are running.
    /// </summary>
    public void UpdateActiveTransfersCount(int activeCount)
    {
        lock (_sync)
        {
            _activeTransfersCount = Math.Max(0, activeCount);

            if (_activeTransfersCount > 0)
            {
                ApplyHighPerformanceState();
            }
            else
            {
                RevertToNormalState();
            }
        }
    }

    private void ApplyHighPerformanceState()
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            if (proc.PriorityClass != ProcessPriorityClass.AboveNormal)
            {
                proc.PriorityClass = ProcessPriorityClass.AboveNormal;
                Log.Information("[ProcessPriority] Elevated process priority to AboveNormal for active downloads.");
            }

            if (OperatingSystem.IsWindows())
            {
                uint prev = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
                if (prev != 0)
                {
                    Log.Debug("[ProcessPriority] SetThreadExecutionState active: prevented system sleep during downloads.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ProcessPriority] Error applying high-performance execution state.");
        }
    }

    private void RevertToNormalState()
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            if (proc.PriorityClass != ProcessPriorityClass.Normal)
            {
                proc.PriorityClass = ProcessPriorityClass.Normal;
                Log.Information("[ProcessPriority] Reverted process priority to Normal (idle).");
            }

            if (OperatingSystem.IsWindows())
            {
                uint prev = SetThreadExecutionState(ES_CONTINUOUS);
                if (prev != 0)
                {
                    Log.Debug("[ProcessPriority] SetThreadExecutionState reverted: system sleep permitted.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ProcessPriority] Error reverting execution state to normal.");
        }
    }
}
