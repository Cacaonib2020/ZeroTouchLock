using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZeroTouchLock.Configuration;
using ZeroTouchLock.Core;

namespace ZeroTouchLock.Tests;

public sealed class MockWorkstationLocker : IWorkstationLocker
{
    public int LockCallCount { get; private set; }
    public bool LockResult { get; set; } = true;

    public bool LockWorkStation()
    {
        LockCallCount++;
        return LockResult;
    }
}

public sealed class MockHumanActivityGuard : IHumanActivityGuard
{
    public TimeSpan IdleTime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan GetIdleTime() => IdleTime;

    public bool IsUserActive(TimeSpan maxIdleThreshold) => IdleTime < maxIdleThreshold;
}

public sealed class MockSessionMonitor : ISessionMonitor
{
    public bool IsSessionLocked { get; set; }
    public event EventHandler<bool>? SessionLockStateChanged;

    public void TriggerSessionLock(bool locked)
    {
        IsSessionLocked = locked;
        SessionLockStateChanged?.Invoke(this, locked);
    }

    public void Dispose() { }
}

public class StateMachineTests
{
    private readonly ThresholdsConfig _thresholds;
    private readonly FeaturesConfig _features;
    private readonly MockWorkstationLocker _locker;
    private readonly MockHumanActivityGuard _activityGuard;
    private readonly MockSessionMonitor _sessionMonitor;

    public StateMachineTests()
    {
        _thresholds = new ThresholdsConfig
        {
            PresentRssiDbm = -68.0,
            WarningRssiDbm = -72.0,
            AwayRssiDbm = -78.0,
            WarningDurationMs = 2000,
            AwayDurationMs = 3500,
            UserInputGuardMs = 3000
        };

        _features = new FeaturesConfig
        {
            EnableStealthOverlay = true,
            OverlayOpacity = 0.75
        };

        _locker = new MockWorkstationLocker();
        _activityGuard = new MockHumanActivityGuard();
        _sessionMonitor = new MockSessionMonitor();
    }

    private ProximityStateMachine CreateStateMachine()
    {
        return new ProximityStateMachine(
            _thresholds,
            _features,
            _locker,
            _activityGuard,
            _sessionMonitor,
            NullLogger<ProximityStateMachine>.Instance);
    }

    [Fact]
    public void StrongRssi_MaintainsPresentState()
    {
        var sm = CreateStateMachine();
        DateTime now = DateTime.UtcNow;

        sm.Evaluate(-62.0, now);

        Assert.Equal(ProximityState.Present, sm.CurrentState);
        Assert.Equal(0, _locker.LockCallCount);
    }

    [Fact]
    public void WarningRssi_Requires2000msToEnterWarningState()
    {
        var sm = CreateStateMachine();
        DateTime start = DateTime.UtcNow;

        // RSSI drops to -73 dBm (between -68 and -78)
        sm.Evaluate(-73.0, start);
        Assert.Equal(ProximityState.Present, sm.CurrentState); // Not yet 2.0s

        // 1000ms later: still in Present
        sm.Evaluate(-73.0, start.AddMilliseconds(1000));
        Assert.Equal(ProximityState.Present, sm.CurrentState);

        // 2100ms later: duration condition met -> Warning state!
        bool shieldRequested = false;
        sm.StealthShieldRequested += (s, o) => shieldRequested = true;

        sm.Evaluate(-73.0, start.AddMilliseconds(2100));
        Assert.Equal(ProximityState.Warning, sm.CurrentState);
        Assert.True(shieldRequested);
    }

    [Fact]
    public void WarningRecovery_ImmediatelyClearsShieldAndReturnsToPresent()
    {
        var sm = CreateStateMachine();
        DateTime start = DateTime.UtcNow;

        sm.Evaluate(-73.0, start);
        sm.Evaluate(-73.0, start.AddMilliseconds(2100));
        Assert.Equal(ProximityState.Warning, sm.CurrentState);

        bool shieldDismissed = false;
        sm.StealthShieldDismissed += (s, e) => shieldDismissed = true;

        // Signal recovers to -65 dBm (>= -68 dBm)
        sm.Evaluate(-65.0, start.AddMilliseconds(2200));

        Assert.Equal(ProximityState.Present, sm.CurrentState);
        Assert.True(shieldDismissed);
    }

    [Fact]
    public void AwayRssi_WhenUserIsIdle_ExecutesLockWorkStationAfter3500ms()
    {
        var sm = CreateStateMachine();
        DateTime start = DateTime.UtcNow;
        _activityGuard.IdleTime = TimeSpan.FromSeconds(30); // User is idle

        sm.Evaluate(-82.0, start);
        Assert.NotEqual(ProximityState.Locked, sm.CurrentState);
        Assert.Equal(0, _locker.LockCallCount);

        // 3600ms later (> 3500ms Away duration)
        sm.Evaluate(-82.0, start.AddMilliseconds(3600));

        Assert.Equal(ProximityState.Locked, sm.CurrentState);
        Assert.Equal(1, _locker.LockCallCount);
    }

    [Fact]
    public void AwayRssi_WhenUserIsActive_SuppressesLockWorkStation()
    {
        var sm = CreateStateMachine();
        DateTime start = DateTime.UtcNow;

        // USER IS ACTIVELY TYPING! Idle time is only 1.2s (< 3.0s threshold)
        _activityGuard.IdleTime = TimeSpan.FromMilliseconds(1200);

        bool suppressionNotified = false;
        sm.LockSuppressedByActivity += (s, idle) => suppressionNotified = true;

        // Signal drops to -85 dBm at t=0
        sm.Evaluate(-85.0, start);

        // 4000ms later with weak signal (exceeding 3500ms away duration)
        sm.Evaluate(-85.0, start.AddMilliseconds(4000));

        // LockWorkStation MUST NOT BE CALLED!
        Assert.Equal(0, _locker.LockCallCount);
        Assert.Equal(ProximityState.Warning, sm.CurrentState);
        Assert.True(suppressionNotified);
    }

    [Fact]
    public void SessionUnlock_ResetsStateToPresent()
    {
        var sm = CreateStateMachine();
        DateTime now = DateTime.UtcNow;

        _sessionMonitor.TriggerSessionLock(true);
        Assert.Equal(ProximityState.Locked, sm.CurrentState);

        // User unlocks PC
        _sessionMonitor.TriggerSessionLock(false);
        Assert.Equal(ProximityState.Present, sm.CurrentState);
    }
}
