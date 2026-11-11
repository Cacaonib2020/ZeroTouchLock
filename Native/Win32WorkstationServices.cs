using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ZeroTouchLock.Core;

namespace ZeroTouchLock.Native;

/// <summary>
/// Implements workstation locking via user32.dll LockWorkStation.
/// </summary>
public sealed class Win32WorkstationLocker : IWorkstationLocker
{
    private readonly ILogger<Win32WorkstationLocker> _logger;

    public Win32WorkstationLocker(ILogger<Win32WorkstationLocker> logger)
    {
        _logger = logger;
    }

    public bool LockWorkStation()
    {
        _logger.LogWarning("Executing Win32 LockWorkStation()...");
        bool result = Win32Api.LockWorkStation();
        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            _logger.LogError("LockWorkStation failed with Win32 error code: {ErrorCode}", error);
        }
        else
        {
            _logger.LogInformation("LockWorkStation successfully invoked.");
        }
        return result;
    }
}

/// <summary>
/// Implements human activity guarding via user32.dll GetLastInputInfo.
/// </summary>
public sealed class Win32HumanActivityGuard : IHumanActivityGuard
{
    private readonly ILogger<Win32HumanActivityGuard> _logger;

    public Win32HumanActivityGuard(ILogger<Win32HumanActivityGuard> logger)
    {
        _logger = logger;
    }

    public TimeSpan GetIdleTime()
    {
        var lii = new Win32Api.LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf<Win32Api.LASTINPUTINFO>();

        if (Win32Api.GetLastInputInfo(ref lii))
        {
            uint currentTick = unchecked((uint)Environment.TickCount);
            uint elapsedMs = unchecked(currentTick - lii.dwTime);
            return TimeSpan.FromMilliseconds(elapsedMs);
        }

        _logger.LogWarning("GetLastInputInfo returned false. Assuming no recent activity.");
        return TimeSpan.MaxValue;
    }

    public bool IsUserActive(TimeSpan maxIdleThreshold)
    {
        TimeSpan idleTime = GetIdleTime();
        bool active = idleTime < maxIdleThreshold;
        if (active)
        {
            _logger.LogDebug("User activity detected within guard window. IdleTime: {IdleMs:F0}ms < Threshold: {ThresholdMs:F0}ms",
                idleTime.TotalMilliseconds, maxIdleThreshold.TotalMilliseconds);
        }
        return active;
    }
}
