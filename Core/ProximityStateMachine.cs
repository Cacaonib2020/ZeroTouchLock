using Microsoft.Extensions.Logging;
using ZeroTouchLock.Configuration;

namespace ZeroTouchLock.Core;

public sealed class ProximityStateChangedEventArgs : EventArgs
{
    public ProximityState PreviousState { get; }
    public ProximityState NewState { get; }
    public string Reason { get; }
    public double EstimatedRssi { get; }

    public ProximityStateChangedEventArgs(ProximityState previousState, ProximityState newState, string reason, double estimatedRssi)
    {
        PreviousState = previousState;
        NewState = newState;
        Reason = reason;
        EstimatedRssi = estimatedRssi;
    }
}

/// <summary>
/// Hysteresis state machine that manages transitions between Present, Warning, Away,
/// Locked, and Paused states based on filtered RSSI, GATT connection, and user activity.
/// </summary>
public sealed class ProximityStateMachine
{
    private readonly ThresholdsConfig _thresholds;
    private readonly FeaturesConfig _features;
    private readonly IWorkstationLocker _workstationLocker;
    private readonly IHumanActivityGuard _activityGuard;
    private readonly ISessionMonitor _sessionMonitor;
    private readonly ILogger<ProximityStateMachine> _logger;

    private ProximityState _currentState = ProximityState.Present;
    private bool _isPaused;
    private bool _isGattConnected = true;
    private DateTime? _gattDisconnectTime;

    private DateTime? _warningStartTime;
    private DateTime? _awayStartTime;
    private readonly object _syncLock = new();

    public ProximityState CurrentState
    {
        get
        {
            lock (_syncLock) return _currentState;
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_syncLock) return _isPaused;
        }
    }

    public bool IsGattConnected
    {
        get
        {
            lock (_syncLock) return _isGattConnected;
        }
    }

    public event EventHandler<ProximityStateChangedEventArgs>? StateChanged;
    public event EventHandler<TimeSpan>? LockSuppressedByActivity;
    public event EventHandler<double>? StealthShieldRequested;
    public event EventHandler? StealthShieldDismissed;

    public ProximityStateMachine(
        ThresholdsConfig thresholds,
        FeaturesConfig features,
        IWorkstationLocker workstationLocker,
        IHumanActivityGuard activityGuard,
        ISessionMonitor sessionMonitor,
        ILogger<ProximityStateMachine> logger)
    {
        _thresholds = thresholds;
        _features = features;
        _workstationLocker = workstationLocker;
        _activityGuard = activityGuard;
        _sessionMonitor = sessionMonitor;
        _logger = logger;

        _sessionMonitor.SessionLockStateChanged += OnSessionLockStateChanged;
        if (_sessionMonitor.IsSessionLocked)
        {
            _currentState = ProximityState.Locked;
        }
    }

    /// <summary>
    /// Evaluates the current state given a new filtered RSSI reading and current timestamp.
    /// </summary>
    public void Evaluate(double estimatedRssi, DateTime now)
    {
        lock (_syncLock)
        {
            if (_isPaused)
            {
                if (_currentState != ProximityState.Paused)
                {
                    TransitionTo(ProximityState.Paused, "Monitoring paused by user", estimatedRssi);
                    StealthShieldDismissed?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            if (_sessionMonitor.IsSessionLocked)
            {
                if (_currentState != ProximityState.Locked)
                {
                    TransitionTo(ProximityState.Locked, "OS session is locked", estimatedRssi);
                    StealthShieldDismissed?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            if (_currentState == ProximityState.Locked)
            {
                // When locked, remain locked until session unlock event fires
                return;
            }

            // 1. Check Present Condition: RSSI >= PresentRssiDbm (-68 dBm)
            if (estimatedRssi >= _thresholds.PresentRssiDbm)
            {
                _warningStartTime = null;
                _awayStartTime = null;

                if (_currentState != ProximityState.Present)
                {
                    TransitionTo(ProximityState.Present, $"RSSI ({estimatedRssi:F1} dBm) >= Present threshold ({_thresholds.PresentRssiDbm} dBm)", estimatedRssi);
                    StealthShieldDismissed?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            // 2. Check Away Condition: RSSI <= AwayRssiDbm (-78 dBm) or GATT disconnected >= 4.0s
            bool isRssiAwayZone = estimatedRssi <= _thresholds.AwayRssiDbm;
            bool isGattDisconnectedTimeout = !_isGattConnected && _gattDisconnectTime.HasValue &&
                                             (now - _gattDisconnectTime.Value).TotalMilliseconds >= 4000.0;

            if (isRssiAwayZone || isGattDisconnectedTimeout)
            {
                _warningStartTime ??= now;
                _awayStartTime ??= now;

                bool awayDurationMet = (now - _awayStartTime.Value).TotalMilliseconds >= _thresholds.AwayDurationMs;

                if (awayDurationMet || isGattDisconnectedTimeout)
                {
                    // Evaluate Human Activity Guard (GetLastInputInfo)
                    TimeSpan inputGuardThreshold = TimeSpan.FromMilliseconds(_thresholds.UserInputGuardMs);
                    if (_activityGuard.IsUserActive(inputGuardThreshold))
                    {
                        TimeSpan idleTime = _activityGuard.GetIdleTime();
                        _logger.LogInformation(
                            "Away threshold reached but lock suppressed by user input guard. Idle: {Idle:F1}s < Guard: {Guard:F1}s",
                            idleTime.TotalSeconds, inputGuardThreshold.TotalSeconds);

                        LockSuppressedByActivity?.Invoke(this, idleTime);

                        // Keep warning overlay active to alert user about weak signal
                        if (_currentState != ProximityState.Warning && _features.EnableStealthOverlay)
                        {
                            TransitionTo(ProximityState.Warning, "Signal weak but lock guarded by user activity", estimatedRssi);
                            StealthShieldRequested?.Invoke(this, _features.OverlayOpacity);
                        }
                        return;
                    }

                    // User is absent: execute workstation lock!
                    string triggerReason = awayDurationMet
                        ? $"RSSI ({estimatedRssi:F1} dBm) <= Away threshold ({_thresholds.AwayRssiDbm} dBm) for {_thresholds.AwayDurationMs}ms"
                        : "GATT connection lost for >= 4000ms";

                    TransitionTo(ProximityState.Away, triggerReason, estimatedRssi);
                    _logger.LogWarning("Triggering automatic workstation lock: {Reason}", triggerReason);

                    bool lockSuccess = _workstationLocker.LockWorkStation();
                    if (lockSuccess)
                    {
                        TransitionTo(ProximityState.Locked, "Workstation locked successfully", estimatedRssi);
                        StealthShieldDismissed?.Invoke(this, EventArgs.Empty);
                    }
                    return;
                }

                // Within grace period before Away triggers: display Warning overlay
                if (_currentState != ProximityState.Warning)
                {
                    TransitionTo(ProximityState.Warning, "Signal falling rapidly towards away threshold", estimatedRssi);
                    if (_features.EnableStealthOverlay)
                    {
                        StealthShieldRequested?.Invoke(this, _features.OverlayOpacity);
                    }
                }
                return;
            }

            // 3. Check Warning Zone: -78 dBm < estimatedRssi < -68 dBm
            _awayStartTime = null; // Reset away timer
            _warningStartTime ??= now;

            if ((now - _warningStartTime.Value).TotalMilliseconds >= _thresholds.WarningDurationMs)
            {
                if (_currentState != ProximityState.Warning)
                {
                    TransitionTo(ProximityState.Warning,
                        $"RSSI ({estimatedRssi:F1} dBm) in warning zone for >= {_thresholds.WarningDurationMs}ms",
                        estimatedRssi);

                    if (_features.EnableStealthOverlay)
                    {
                        StealthShieldRequested?.Invoke(this, _features.OverlayOpacity);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Updates the GATT connection status.
    /// </summary>
    public void UpdateGattConnection(bool isConnected, DateTime now)
    {
        lock (_syncLock)
        {
            if (_isGattConnected != isConnected)
            {
                _isGattConnected = isConnected;
                if (!isConnected)
                {
                    _gattDisconnectTime = now;
                    _logger.LogWarning("GATT disconnected at {Time}. Disconnect timer started.", now);
                }
                else
                {
                    _gattDisconnectTime = null;
                    _logger.LogInformation("GATT reconnected at {Time}.", now);
                }
            }
        }
    }

    /// <summary>
    /// Toggles pause state.
    /// </summary>
    public void SetPaused(bool paused)
    {
        lock (_syncLock)
        {
            _isPaused = paused;
            if (paused)
            {
                TransitionTo(ProximityState.Paused, "User paused dynamic lock", -100);
                StealthShieldDismissed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _warningStartTime = null;
                _awayStartTime = null;
                TransitionTo(ProximityState.Present, "User resumed dynamic lock", -60);
            }
        }
    }

    private void OnSessionLockStateChanged(object? sender, bool isLocked)
    {
        lock (_syncLock)
        {
            if (isLocked)
            {
                TransitionTo(ProximityState.Locked, "OS session locked", -100);
                StealthShieldDismissed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _warningStartTime = null;
                _awayStartTime = null;
                TransitionTo(ProximityState.Present, "OS session unlocked", -60);
            }
        }
    }

    private void TransitionTo(ProximityState newState, string reason, double rssi)
    {
        if (_currentState == newState) return;

        ProximityState oldState = _currentState;
        _currentState = newState;
        _logger.LogInformation("State transition: {OldState} -> {NewState}. Reason: {Reason} (RSSI: {Rssi:F1} dBm)",
            oldState, newState, reason, rssi);

        StateChanged?.Invoke(this, new ProximityStateChangedEventArgs(oldState, newState, reason, rssi));
    }
}
