using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroTouchLock.Configuration;
using ZeroTouchLock.Core;
using ZeroTouchLock.Native;
using ZeroTouchLock.UI;

namespace ZeroTouchLock;

/// <summary>
/// Application startup and lifecycle coordinator.
/// Configures background system tray monitoring, dependency injection, and single-instance mutex.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Global\ZeroTouchLock_SingleInstance_Mutex_99812A";
    private Mutex? _mutex;
    private ServiceProvider? _serviceProvider;
    private ILogger<App>? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Single Instance Check via Global Named Mutex
        _mutex = new Mutex(true, MutexName, out bool isOnlyInstance);
        if (!isOnlyInstance)
        {
            MessageBox.Show(
                "Zero-Touch BLE Dynamic Lock is already running in the system tray.",
                "ZeroTouchLock",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 2. Build Configuration from appsettings.json
        string baseDir = AppContext.BaseDirectory;
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddCommandLine(e.Args)
            .Build();

        var appSettings = new AppSettings();
        configuration.Bind(appSettings);

        // 3. Configure Dependency Injection
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                options.SingleLine = true;
            });
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Register Configuration
        services.AddSingleton(appSettings);
        services.AddSingleton(appSettings.TargetDevice);
        services.AddSingleton(appSettings.KalmanFilter);
        services.AddSingleton(appSettings.Thresholds);
        services.AddSingleton(appSettings.Features);

        // Native System Integrations
        services.AddSingleton<IWorkstationLocker, Win32WorkstationLocker>();
        services.AddSingleton<IHumanActivityGuard, Win32HumanActivityGuard>();
        services.AddSingleton<ISessionMonitor, SessionMonitor>();

        // Core Mathematical Processing & Proximity State Machine
        services.AddSingleton(sp =>
        {
            var kfConfig = sp.GetRequiredService<KalmanFilterConfig>();
            return new KalmanFilter1D(kfConfig.ProcessNoiseQ, kfConfig.MeasurementNoiseR);
        });
        services.AddSingleton<ProximityStateMachine>();
        services.AddSingleton<BleProximityMonitor>();

        // UI Components
        services.AddSingleton<StealthOverlayForm>();
        services.AddSingleton<TrayIconManager>();
        services.AddSingleton<DashboardWindow>();

        _serviceProvider = services.BuildServiceProvider();
        _logger = _serviceProvider.GetRequiredService<ILogger<App>>();

        _logger.LogInformation("==================================================");
        _logger.LogInformation("Zero-Touch BLE Dynamic Lock System (App-less Edition)");
        _logger.LogInformation("Target OS: Windows 10/11 | Architecture: .NET 8 WPF/WinForms");
        _logger.LogInformation("==================================================");

        try
        {
            // Initialize Tray Icon and Dashboard Window
            var trayIcon = _serviceProvider.GetRequiredService<TrayIconManager>();
            var dashboardWindow = _serviceProvider.GetRequiredService<DashboardWindow>();
            trayIcon.RegisterDashboardWindow(dashboardWindow);

            // Start BLE Proximity Monitor
            var bleMonitor = _serviceProvider.GetRequiredService<BleProximityMonitor>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await bleMonitor.StartAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start BLE proximity monitor.");
                }
            });

            _logger.LogInformation("Application started in system tray. Double-click tray icon to open dashboard.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error during application startup.");
            MessageBox.Show(
                $"Fatal startup error: {ex.Message}",
                "ZeroTouchLock Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Shutting down Zero-Touch BLE Dynamic Lock...");

        if (_serviceProvider != null)
        {
            try
            {
                _serviceProvider.GetService<BleProximityMonitor>()?.Dispose();
                _serviceProvider.GetService<TrayIconManager>()?.Dispose();
                _serviceProvider.GetService<ISessionMonitor>()?.Dispose();
                _serviceProvider.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error disposing services during shutdown.");
            }
        }

        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }

        base.OnExit(e);
    }
}
