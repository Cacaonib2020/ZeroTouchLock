using System.Drawing.Drawing2D;
using Microsoft.Extensions.Logging;
using ZeroTouchLock.Core;

namespace ZeroTouchLock.UI;

/// <summary>
/// Manages the Windows taskbar system tray notification icon, showing real-time proximity
/// status, smoothed RSSI, device connectivity, and context menus for user control.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _openDashboardMenuItem;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _deviceMenuItem;
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly ToolStripMenuItem _lockNowMenuItem;
    private readonly ToolStripMenuItem _rescanMenuItem;
    private readonly ToolStripMenuItem _exitMenuItem;

    private readonly ProximityStateMachine _stateMachine;
    private readonly BleProximityMonitor _bleMonitor;
    private readonly StealthOverlayForm _stealthOverlay;
    private readonly IWorkstationLocker _workstationLocker;
    private readonly ILogger<TrayIconManager> _logger;
    private DashboardWindow? _dashboardWindow;

    private double _latestEstimatedRssi = -100.0;
    private double _latestRawRssi = -100.0;
    private Icon? _currentGeneratedIcon;
    private bool _disposed;

    public TrayIconManager(
        ProximityStateMachine stateMachine,
        BleProximityMonitor bleMonitor,
        StealthOverlayForm stealthOverlay,
        IWorkstationLocker workstationLocker,
        ILogger<TrayIconManager> logger)
    {
        _stateMachine = stateMachine;
        _bleMonitor = bleMonitor;
        _stealthOverlay = stealthOverlay;
        _workstationLocker = workstationLocker;
        _logger = logger;

        _contextMenu = new ContextMenuStrip();

        _openDashboardMenuItem = new ToolStripMenuItem("Open Dashboard")
        {
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        _openDashboardMenuItem.Click += (s, e) => ShowDashboard();

        _statusMenuItem = new ToolStripMenuItem("Status: Initializing...")
        {
            Enabled = false
        };

        _deviceMenuItem = new ToolStripMenuItem("Target: Searching...")
        {
            Enabled = false
        };

        _pauseMenuItem = new ToolStripMenuItem("Pause Monitoring")
        {
            CheckOnClick = true
        };
        _pauseMenuItem.Click += OnPauseClicked;

        _lockNowMenuItem = new ToolStripMenuItem("Lock Now");
        _lockNowMenuItem.Click += (s, e) =>
        {
            _logger.LogInformation("Manual workstation lock requested from tray menu.");
            _workstationLocker.LockWorkStation();
        };

        _rescanMenuItem = new ToolStripMenuItem("Rescan Paired Devices");
        _rescanMenuItem.Click += async (s, e) =>
        {
            _logger.LogInformation("Manual device rescan triggered from tray menu.");
            await _bleMonitor.EnumeratePairedDevicesAsync();
        };

        _exitMenuItem = new ToolStripMenuItem("Exit");
        _exitMenuItem.Click += (s, e) =>
        {
            _logger.LogInformation("Application exit requested from tray menu.");
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
        };

        _contextMenu.Items.AddRange(new ToolStripItem[]
        {
            _openDashboardMenuItem,
            new ToolStripSeparator(),
            _statusMenuItem,
            _deviceMenuItem,
            new ToolStripSeparator(),
            _pauseMenuItem,
            _lockNowMenuItem,
            _rescanMenuItem,
            new ToolStripSeparator(),
            _exitMenuItem
        });

        _notifyIcon = new NotifyIcon
        {
            Text = "ZeroTouchLock - Initializing...",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        // Open dashboard on tray click or double click
        _notifyIcon.DoubleClick += (s, e) => ShowDashboard();
        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowDashboard();
            }
        };

        // Wire up event subscriptions
        _stateMachine.StateChanged += OnStateChanged;
        _stateMachine.StealthShieldRequested += (s, opacity) => _stealthOverlay.ShowShield(opacity);
        _stateMachine.StealthShieldDismissed += (s, e) => _stealthOverlay.HideShield();

        _bleMonitor.RssiUpdated += OnRssiUpdated;
        _bleMonitor.TargetDeviceIdentified += OnTargetDeviceIdentified;

        UpdateDisplay(_stateMachine.CurrentState, -100.0, -100.0);
    }

    public void RegisterDashboardWindow(DashboardWindow dashboardWindow)
    {
        _dashboardWindow = dashboardWindow;
    }

    public void ShowDashboard()
    {
        if (_dashboardWindow == null) return;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _dashboardWindow.Show();
            if (_dashboardWindow.WindowState == System.Windows.WindowState.Minimized)
            {
                _dashboardWindow.WindowState = System.Windows.WindowState.Normal;
            }
            _dashboardWindow.Activate();
        });
    }

    private void OnPauseClicked(object? sender, EventArgs e)
    {
        bool isPaused = _pauseMenuItem.Checked;
        _stateMachine.SetPaused(isPaused);
        _logger.LogInformation("Dynamic lock paused state toggled to: {IsPaused}", isPaused);
    }

    private void OnStateChanged(object? sender, ProximityStateChangedEventArgs e)
    {
        UpdateDisplay(e.NewState, _latestRawRssi, e.EstimatedRssi);
    }

    private void OnRssiUpdated(object? sender, RssiUpdateEventArgs e)
    {
        _latestRawRssi = e.RawRssi;
        _latestEstimatedRssi = e.EstimatedRssi;
        UpdateDisplay(_stateMachine.CurrentState, e.RawRssi, e.EstimatedRssi);
    }

    private void OnTargetDeviceIdentified(object? sender, TargetDeviceInfoEventArgs e)
    {
        string text = $"Target: {e.DeviceName} (Paired)";
        if (_deviceMenuItem.GetCurrentParent() != null && _deviceMenuItem.GetCurrentParent()!.InvokeRequired)
        {
            _deviceMenuItem.GetCurrentParent()!.BeginInvoke(new Action(() => _deviceMenuItem.Text = text));
        }
        else
        {
            _deviceMenuItem.Text = text;
        }
    }

    private void UpdateDisplay(ProximityState state, double rawRssi, double estimatedRssi)
    {
        Color statusColor = state switch
        {
            ProximityState.Present => Color.FromArgb(34, 197, 94),    // Green-500
            ProximityState.Warning => Color.FromArgb(245, 158, 11),   // Amber-500
            ProximityState.Away => Color.FromArgb(239, 68, 68),      // Red-500
            ProximityState.Locked => Color.FromArgb(59, 130, 246),    // Blue-500
            ProximityState.Paused => Color.FromArgb(156, 163, 175),   // Gray-400
            _ => Color.Gray
        };

        UpdateTrayIcon(statusColor);

        string rssiStr = estimatedRssi > -99 ? $"{estimatedRssi:F1} dBm" : "-- dBm";
        _statusMenuItem.Text = $"Status: {state} ({rssiStr})";

        double distance = DistanceEstimator.EstimateDistanceMeters(estimatedRssi);
        string distStr = DistanceEstimator.FormatDistance(distance);

        // Section 7.3: ZeroTouchLock - [PRESENT] -63.5 dBm (1.2m)
        string tooltip = $"ZeroTouchLock - [{state.ToString().ToUpperInvariant()}] {rssiStr} ({distStr})";

        if (tooltip.Length >= 127)
        {
            tooltip = tooltip[..124] + "...";
        }
        _notifyIcon.Text = tooltip;
    }

    private void UpdateTrayIcon(Color accentColor)
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Dark circular background
                using var bgBrush = new SolidBrush(Color.FromArgb(220, 20, 24, 34));
                g.FillEllipse(bgBrush, 2, 2, 28, 28);

                // Accent colored border & inner status pulse dot
                using var borderPen = new Pen(accentColor, 2.5f);
                g.DrawEllipse(borderPen, 3, 3, 26, 26);

                using var pulseBrush = new SolidBrush(accentColor);
                g.FillEllipse(pulseBrush, 10, 10, 12, 12);
            }

            IntPtr hIcon = bmp.GetHicon();
            Icon newIcon = Icon.FromHandle(hIcon);

            _notifyIcon.Icon = newIcon;

            _currentGeneratedIcon?.Dispose();
            _currentGeneratedIcon = newIcon;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to render dynamic tray icon.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _currentGeneratedIcon?.Dispose();
    }
}
