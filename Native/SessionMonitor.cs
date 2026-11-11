using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using ZeroTouchLock.Core;

namespace ZeroTouchLock.Native;

/// <summary>
/// Monitors Windows session state changes (Lock / Unlock) using SystemEvents.SessionSwitch.
/// </summary>
public sealed class SessionMonitor : ISessionMonitor
{
    private readonly ILogger<SessionMonitor> _logger;
    private bool _isSessionLocked;
    private bool _disposed;

    public bool IsSessionLocked => _isSessionLocked;

    public event EventHandler<bool>? SessionLockStateChanged;

    public SessionMonitor(ILogger<SessionMonitor> logger)
    {
        _logger = logger;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _logger.LogInformation("SessionMonitor initialized and subscribed to SystemEvents.SessionSwitch.");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _logger.LogInformation("OS Session Lock detected.");
                _isSessionLocked = true;
                SessionLockStateChanged?.Invoke(this, true);
                break;

            case SessionSwitchReason.SessionUnlock:
                _logger.LogInformation("OS Session Unlock detected.");
                _isSessionLocked = false;
                SessionLockStateChanged?.Invoke(this, false);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _logger.LogInformation("SessionMonitor disposed.");
    }
}
