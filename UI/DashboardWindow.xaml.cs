using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using ZeroTouchLock.Configuration;
using ZeroTouchLock.Core;

namespace ZeroTouchLock.UI;

/// <summary>
/// Modern Fluent telemetry and calibration dashboard window.
/// Includes real-time dual-waveform oscilloscope (Raw vs Kalman Filtered RSSI),
/// dynamic distance indicator, human activity guard monitor, and hot-reloading calibration controls.
/// </summary>
public partial class DashboardWindow : Window
{
    private const int MaxSamples = 120;
    private const double MinDbm = -100.0;
    private const double MaxDbm = -30.0;

    private readonly AppSettings _settings;
    private readonly KalmanFilter1D _kalmanFilter;
    private readonly ProximityStateMachine _stateMachine;
    private readonly BleProximityMonitor _bleMonitor;
    private readonly IHumanActivityGuard _activityGuard;
    private readonly IWorkstationLocker _workstationLocker;
    private readonly StealthOverlayForm _stealthOverlay;
    private readonly ILogger<DashboardWindow> _logger;

    private readonly List<double> _rawHistory = new(MaxSamples);
    private readonly List<double> _filteredHistory = new(MaxSamples);

    private readonly DispatcherTimer _idleTimer;
    private double _lastRawRssi = -100.0;
    private double _lastFilteredRssi = -100.0;
    private bool _isInitializingSliders = true;

    public DashboardWindow(
        AppSettings settings,
        KalmanFilter1D kalmanFilter,
        ProximityStateMachine stateMachine,
        BleProximityMonitor bleMonitor,
        IHumanActivityGuard activityGuard,
        IWorkstationLocker workstationLocker,
        StealthOverlayForm stealthOverlay,
        ILogger<DashboardWindow> logger)
    {
        InitializeComponent();

        _settings = settings;
        _kalmanFilter = kalmanFilter;
        _stateMachine = stateMachine;
        _bleMonitor = bleMonitor;
        _activityGuard = activityGuard;
        _workstationLocker = workstationLocker;
        _stealthOverlay = stealthOverlay;
        _logger = logger;

        // Initialize sliders from loaded configuration
        LoadConfigIntoSliders();

        // Wire up telemetry and state machine event handlers
        _bleMonitor.RssiUpdated += OnBleRssiUpdated;
        _stateMachine.StateChanged += OnStateMachineChanged;

        // Polling timer for human activity guard idle time (runs at 4 Hz only when visible)
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _idleTimer.Tick += OnIdleTimerTick;

        IsVisibleChanged += OnWindowVisibilityChanged;
    }

    /// <summary>
    /// Minimizes window to system tray when user clicks X (Closing event) instead of exiting.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        _logger.LogInformation("Dashboard window minimized to taskbar system tray.");
    }

    private void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _idleTimer.Start();
            UpdateStateBadge(_stateMachine.CurrentState);
            RedrawOscilloscope();
        }
        else
        {
            _idleTimer.Stop();
        }
    }

    private void LoadConfigIntoSliders()
    {
        _isInitializingSliders = true;

        WarningSlider.Value = _settings.Thresholds.WarningRssiDbm;
        WarningSliderVal.Text = $"{_settings.Thresholds.WarningRssiDbm:F0} dBm";

        AwaySlider.Value = _settings.Thresholds.AwayRssiDbm;
        AwaySliderVal.Text = $"{_settings.Thresholds.AwayRssiDbm:F0} dBm";

        AwayDurationSlider.Value = _settings.Thresholds.AwayDurationMs / 1000.0;
        AwayDurationSliderVal.Text = $"{AwayDurationSlider.Value:F1}s";

        KalmanRSlider.Value = _settings.KalmanFilter.MeasurementNoiseR;
        KalmanRSliderVal.Text = $"{_settings.KalmanFilter.MeasurementNoiseR:F1}";

        EnableOverlayCheck.IsChecked = _settings.Features.EnableStealthOverlay;

        _isInitializingSliders = false;
    }

    private void OnBleRssiUpdated(object? sender, RssiUpdateEventArgs e)
    {
        _lastRawRssi = e.RawRssi;
        _lastFilteredRssi = e.EstimatedRssi;

        // On-Demand Rendering: if window is hidden, do NOT dispatch to UI thread!
        if (!IsVisible) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (!IsVisible) return;

            // Push to rolling history
            if (_rawHistory.Count >= MaxSamples) _rawHistory.RemoveAt(0);
            if (_filteredHistory.Count >= MaxSamples) _filteredHistory.RemoveAt(0);

            _rawHistory.Add(e.RawRssi);
            _filteredHistory.Add(e.EstimatedRssi);

            // Update Metric Text Blocks
            RawRssiText.Text = $"{e.RawRssi:F0} dBm";
            FilteredRssiText.Text = $"{e.EstimatedRssi:F1} dBm";

            // Calculate estimated distance
            double distance = DistanceEstimator.EstimateDistanceMeters(e.EstimatedRssi);
            DistanceText.Text = DistanceEstimator.FormatDistance(distance);

            RedrawOscilloscope();
        }));
    }

    private void OnStateMachineChanged(object? sender, ProximityStateChangedEventArgs e)
    {
        if (!IsVisible) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateStateBadge(e.NewState);
        }));
    }

    private void OnIdleTimerTick(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        TimeSpan idle = _activityGuard.GetIdleTime();
        double idleSec = idle.TotalSeconds;

        if (idleSec > 999.0)
        {
            IdleTimeText.Text = "Idle: --";
        }
        else
        {
            IdleTimeText.Text = $"Idle: {idleSec:F1}s";
        }

        bool isGuarded = idleSec < (_settings.Thresholds.UserInputGuardMs / 1000.0);
        if (isGuarded)
        {
            GuardStatusText.Text = "Guard Active (Lock Suppressed)";
            GuardStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153)); // Green
        }
        else
        {
            GuardStatusText.Text = "Guard Inactive (Idle)";
            GuardStatusText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Slate-400
        }
    }

    private void UpdateStateBadge(ProximityState state)
    {
        StateBadgeText.Text = state.ToString().ToUpperInvariant();

        (Color bg, Color fg) = state switch
        {
            ProximityState.Present => (Color.FromRgb(6, 78, 59), Color.FromRgb(52, 211, 153)),
            ProximityState.Warning => (Color.FromRgb(120, 53, 15), Color.FromRgb(251, 191, 36)),
            ProximityState.Away => (Color.FromRgb(127, 29, 29), Color.FromRgb(248, 113, 113)),
            ProximityState.Locked => (Color.FromRgb(30, 58, 138), Color.FromRgb(96, 165, 250)),
            ProximityState.Paused => (Color.FromRgb(55, 65, 81), Color.FromRgb(156, 163, 175)),
            _ => (Color.FromRgb(30, 41, 59), Color.FromRgb(226, 232, 240))
        };

        StateBadgeBorder.Background = new SolidColorBrush(bg);
        StateBadgeText.Foreground = new SolidColorBrush(fg);

        PauseButton.Content = state == ProximityState.Paused ? "▶️ Resume Lock" : "⏸️ Pause Lock";
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsVisible)
        {
            RedrawOscilloscope();
        }
    }

    /// <summary>
    /// Renders the real-time oscilloscope chart on WPF Canvas using Polyline and Gridlines.
    /// Hardware-accelerated, zero-dependency, lightweight drawing.
    /// </summary>
    private void RedrawOscilloscope()
    {
        double width = OscilloscopeCanvas.ActualWidth;
        double height = OscilloscopeCanvas.ActualHeight;

        if (width <= 10 || height <= 10) return;

        OscilloscopeCanvas.Children.Clear();

        // 1. Draw subtle horizontal gridlines & dBm scale markers
        double[] gridDbm = { -30.0, -50.0, -70.0, -90.0, -100.0 };
        foreach (double dbm in gridDbm)
        {
            double y = DbmToCanvasY(dbm, height);

            var gridLine = new Line
            {
                X1 = 0,
                Y1 = y,
                X2 = width,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(40, 148, 163, 184)),
                StrokeThickness = 1
            };
            OscilloscopeCanvas.Children.Add(gridLine);

            var label = new TextBlock
            {
                Text = $"{dbm:F0} dBm",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(120, 148, 163, 184))
            };
            Canvas.SetLeft(label, 8);
            Canvas.SetTop(label, Math.Max(2, y - 14));
            OscilloscopeCanvas.Children.Add(label);
        }

        // 2. Draw Threshold Lines
        double warningY = DbmToCanvasY(WarningSlider.Value, height);
        var warningLine = new Line
        {
            X1 = 0,
            Y1 = warningY,
            X2 = width,
            Y2 = warningY,
            Stroke = new SolidColorBrush(Color.FromRgb(234, 179, 8)),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 }
        };
        OscilloscopeCanvas.Children.Add(warningLine);

        var warningLabel = new TextBlock
        {
            Text = $"Warning: {WarningSlider.Value:F0} dBm",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(234, 179, 8))
        };
        Canvas.SetRight(warningLabel, 10);
        Canvas.SetTop(warningLabel, Math.Clamp(warningY - 14, 2, height - 20));
        OscilloscopeCanvas.Children.Add(warningLabel);

        double awayY = DbmToCanvasY(AwaySlider.Value, height);
        var awayLine = new Line
        {
            X1 = 0,
            Y1 = awayY,
            X2 = width,
            Y2 = awayY,
            Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 }
        };
        OscilloscopeCanvas.Children.Add(awayLine);

        var awayLabel = new TextBlock
        {
            Text = $"Away: {AwaySlider.Value:F0} dBm",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68))
        };
        Canvas.SetRight(awayLabel, 10);
        Canvas.SetTop(awayLabel, Math.Clamp(awayY - 14, 2, height - 20));
        OscilloscopeCanvas.Children.Add(awayLabel);

        // 3. Draw Waveform Series 1: Raw RSSI Polyline (Thin Cyan)
        if (_rawHistory.Count > 1)
        {
            var rawPoints = new PointCollection(_rawHistory.Count);
            double stepX = width / (MaxSamples - 1);
            int startOffset = MaxSamples - _rawHistory.Count;

            for (int i = 0; i < _rawHistory.Count; i++)
            {
                double x = (startOffset + i) * stepX;
                double y = DbmToCanvasY(_rawHistory[i], height);
                rawPoints.Add(new Point(x, y));
            }

            var rawPolyline = new Polyline
            {
                Points = rawPoints,
                Stroke = new SolidColorBrush(Color.FromArgb(160, 56, 189, 248)),
                StrokeThickness = 1.2
            };
            OscilloscopeCanvas.Children.Add(rawPolyline);
        }

        // 4. Draw Waveform Series 2: Kalman Filtered RSSI Polyline (Vibrant Neon Green)
        if (_filteredHistory.Count > 1)
        {
            var filteredPoints = new PointCollection(_filteredHistory.Count);
            double stepX = width / (MaxSamples - 1);
            int startOffset = MaxSamples - _filteredHistory.Count;

            for (int i = 0; i < _filteredHistory.Count; i++)
            {
                double x = (startOffset + i) * stepX;
                double y = DbmToCanvasY(_filteredHistory[i], height);
                filteredPoints.Add(new Point(x, y));
            }

            var filteredPolyline = new Polyline
            {
                Points = filteredPoints,
                Stroke = new SolidColorBrush(Color.FromRgb(0, 230, 118)),
                StrokeThickness = 2.5
            };
            OscilloscopeCanvas.Children.Add(filteredPolyline);
        }
    }

    private static double DbmToCanvasY(double dbm, double canvasHeight)
    {
        double clamped = Math.Clamp(dbm, MinDbm, MaxDbm);
        double normalized = (clamped - MinDbm) / (MaxDbm - MinDbm);
        // Canvas coordinate: Y=0 is top, Y=Height is bottom
        return canvasHeight * (1.0 - normalized);
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializingSliders) return;

        if (sender == WarningSlider)
        {
            WarningSliderVal.Text = $"{WarningSlider.Value:F0} dBm";
            if (WarningSlider.Value <= AwaySlider.Value)
            {
                AwaySlider.Value = WarningSlider.Value - 1.0;
            }
        }
        else if (sender == AwaySlider)
        {
            AwaySliderVal.Text = $"{AwaySlider.Value:F0} dBm";
            if (AwaySlider.Value >= WarningSlider.Value)
            {
                WarningSlider.Value = AwaySlider.Value + 1.0;
            }
        }
        else if (sender == AwayDurationSlider)
        {
            AwayDurationSliderVal.Text = $"{AwayDurationSlider.Value:F1}s";
        }
        else if (sender == KalmanRSlider)
        {
            KalmanRSliderVal.Text = $"{KalmanRSlider.Value:F1}";
        }

        RedrawOscilloscope();
    }

    private void OnSaveAndApplyClicked(object sender, RoutedEventArgs e)
    {
        // 1. Hot-reload runtime configuration
        _settings.Thresholds.WarningRssiDbm = WarningSlider.Value;
        _settings.Thresholds.AwayRssiDbm = AwaySlider.Value;
        _settings.Thresholds.AwayDurationMs = (int)(AwayDurationSlider.Value * 1000);
        _settings.KalmanFilter.MeasurementNoiseR = KalmanRSlider.Value;
        _settings.Features.EnableStealthOverlay = EnableOverlayCheck.IsChecked ?? true;

        _kalmanFilter.MeasurementNoiseR = _settings.KalmanFilter.MeasurementNoiseR;

        // 2. Persist to appsettings.json
        string appSettingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            AppSettings.SaveToFile(appSettingsPath, _settings);
            SaveStatusText.Text = "✓ Saved & Applied!";
            _logger.LogInformation("Configuration successfully updated and persisted to {Path}", appSettingsPath);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (s, ev) =>
            {
                SaveStatusText.Text = "";
                timer.Stop();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist configuration to {Path}", appSettingsPath);
            SaveStatusText.Text = "Error saving file";
        }
    }

    private void OnCalibrateHereClicked(object sender, RoutedEventArgs e)
    {
        if (_lastFilteredRssi <= -99)
        {
            MessageBox.Show(
                "No BLE signal received yet from target iPhone.\nPlease wait for a valid advertisement before calibrating.",
                "Calibration Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        double currentRssi = _lastFilteredRssi;
        double recommendedWarning = currentRssi - 6.0;
        double recommendedAway = currentRssi - 12.0;

        WarningSlider.Value = Math.Clamp(recommendedWarning, -95, -45);
        AwaySlider.Value = Math.Clamp(recommendedAway, -100, -50);

        OnSaveAndApplyClicked(sender, e);
        _logger.LogInformation("Automatic calibration applied for base RSSI {Base:F1} dBm -> Warning: {Warn:F1}, Away: {Away:F1}",
            currentRssi, WarningSlider.Value, AwaySlider.Value);
    }

    private void OnTestOverlayClicked(object sender, RoutedEventArgs e)
    {
        _stealthOverlay.ShowShield(0.75);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (s, ev) =>
        {
            _stealthOverlay.HideShield();
            timer.Stop();
        };
        timer.Start();
    }

    private void OnPauseToggleClicked(object sender, RoutedEventArgs e)
    {
        bool nextPaused = !_stateMachine.IsPaused;
        _stateMachine.SetPaused(nextPaused);
        UpdateStateBadge(_stateMachine.CurrentState);
    }

    private void OnLockNowClicked(object sender, RoutedEventArgs e)
    {
        _workstationLocker.LockWorkStation();
    }

    private async void OnRescanDevicesClicked(object sender, RoutedEventArgs e)
    {
        await _bleMonitor.EnumeratePairedDevicesAsync();
    }
}
