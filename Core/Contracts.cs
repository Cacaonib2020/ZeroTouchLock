namespace ZeroTouchLock.Core;

/// <summary>
/// Proximity tracking states defined by the hysteresis state machine.
/// </summary>
public enum ProximityState
{
    /// <summary>User is near the workstation (RSSI >= -68 dBm).</summary>
    Present,

    /// <summary>RSSI has dropped into the warning band (-78 dBm &lt; RSSI &lt; -68 dBm) for >= 2.0s.</summary>
    Warning,

    /// <summary>RSSI has dropped below -78 dBm for >= 3.5s or GATT connection lost for >= 4.0s.</summary>
    Away,

    /// <summary>Workstation display is locked. Automatic re-locking is suspended.</summary>
    Locked,

    /// <summary>Proximity lock monitoring is temporarily paused by the user.</summary>
    Paused
}

/// <summary>
/// Abstraction for locking the workstation.
/// </summary>
public interface IWorkstationLocker
{
    bool LockWorkStation();
}

/// <summary>
/// Abstraction for checking the last user input (keyboard/mouse) idle duration.
/// </summary>
public interface IHumanActivityGuard
{
    TimeSpan GetIdleTime();
    bool IsUserActive(TimeSpan maxIdleThreshold);
}

/// <summary>
/// Abstraction for monitoring OS workstation session lock/unlock events.
/// </summary>
public interface ISessionMonitor : IDisposable
{
    bool IsSessionLocked { get; }
    event EventHandler<bool>? SessionLockStateChanged;
}
