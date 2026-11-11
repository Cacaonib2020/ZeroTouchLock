using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Microsoft.Extensions.Logging;
using ZeroTouchLock.Configuration;

namespace ZeroTouchLock.Core;

public sealed class RssiUpdateEventArgs : EventArgs
{
    public double RawRssi { get; }
    public double EstimatedRssi { get; }
    public DateTime Timestamp { get; }

    public RssiUpdateEventArgs(double rawRssi, double estimatedRssi, DateTime timestamp)
    {
        RawRssi = rawRssi;
        EstimatedRssi = estimatedRssi;
        Timestamp = timestamp;
    }
}

public sealed class TargetDeviceInfoEventArgs : EventArgs
{
    public string DeviceName { get; }
    public string DeviceId { get; }
    public bool IsPaired { get; }

    public TargetDeviceInfoEventArgs(string deviceName, string deviceId, bool isPaired)
    {
        DeviceName = deviceName;
        DeviceId = deviceId;
        IsPaired = isPaired;
    }
}

/// <summary>
/// Monitors BLE proximity of a paired iPhone without requiring any companion app.
/// Solves iOS BLE MAC address randomization (RPA / IRK) by resolving incoming advertisement
/// addresses against Windows paired Bluetooth devices, caching resolutions, and maintaining
/// a GATT Keep-Alive session with ANCS / Battery services.
/// </summary>
public sealed class BleProximityMonitor : IDisposable
{
    private sealed class CachedResolution
    {
        public bool IsTarget { get; init; }
        public string DeviceId { get; init; } = string.Empty;
        public string DeviceName { get; init; } = string.Empty;
        public DateTime ResolvedAt { get; init; }
        public DateTime LastSeen { get; set; }
    }

    private readonly AppSettings _settings;
    private readonly KalmanFilter1D _kalmanFilter;
    private readonly ProximityStateMachine _stateMachine;
    private readonly ISessionMonitor _sessionMonitor;
    private readonly ILogger<BleProximityMonitor> _logger;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _activeTargetDevice;
    private GattDeviceService? _keepAliveService;
    private readonly ConcurrentDictionary<ulong, CachedResolution> _addressCache = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _resolvingSemaphores = new();

    private string? _resolvedTargetDeviceId;
    private string? _resolvedTargetDeviceName;
    private System.Threading.Timer? _decayTimer;
    private bool _disposed;

    // ANCS service UUID (Apple Notification Center Service)
    private static readonly Guid AncsServiceUuid = Guid.Parse("7905F431-B5CE-4E99-A40F-4B1E122D00D0");
    // Standard Battery Service UUID
    private static readonly Guid BatteryServiceUuid = Guid.Parse("0000180F-0000-1000-8000-00805F9B34FB");

    public event EventHandler<RssiUpdateEventArgs>? RssiUpdated;
    public event EventHandler<TargetDeviceInfoEventArgs>? TargetDeviceIdentified;
    public event EventHandler<string>? TargetDeviceConnectionLost;

    public string? CurrentTargetName => _resolvedTargetDeviceName;
    public string? CurrentTargetId => _resolvedTargetDeviceId;
    public bool IsTargetConnected => _activeTargetDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public BleProximityMonitor(
        AppSettings settings,
        KalmanFilter1D kalmanFilter,
        ProximityStateMachine stateMachine,
        ISessionMonitor sessionMonitor,
        ILogger<BleProximityMonitor> logger)
    {
        _settings = settings;
        _kalmanFilter = kalmanFilter;
        _stateMachine = stateMachine;
        _sessionMonitor = sessionMonitor;
        _logger = logger;

        _sessionMonitor.SessionLockStateChanged += OnSessionLockStateChanged;
    }

    /// <summary>
    /// Starts the BLE monitor: enumerates paired devices, discovers the iPhone,
    /// and activates the advertisement watcher and decay timer.
    /// </summary>
    public async Task StartAsync()
    {
        _logger.LogInformation("Starting BLE Proximity Monitor...");

        // 1. Enumerate paired BLE devices and identify target iPhone
        await EnumeratePairedDevicesAsync();

        // 2. Setup WinRT BluetoothLEAdvertisementWatcher
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        // Filter: sample every 250ms for low latency responsiveness
        _watcher.SignalStrengthFilter.SamplingInterval = TimeSpan.FromMilliseconds(250);
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += OnWatcherStopped;

        try
        {
            _watcher.Start();
            _logger.LogInformation("BluetoothLEAdvertisementWatcher started in Active mode.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start BluetoothLEAdvertisementWatcher. Ensure Bluetooth is enabled on the PC.");
        }

        // 3. Start periodic decay evaluation timer (every 250ms)
        _decayTimer = new System.Threading.Timer(OnDecayTimerTick, null, 500, 250);
    }

    /// <summary>
    /// Enumerates all Windows paired BLE devices and matches target iPhone.
    /// </summary>
    public async Task EnumeratePairedDevicesAsync()
    {
        _logger.LogInformation("=== Enumerating Windows Paired BLE Devices ===");

        string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);

        _logger.LogInformation("Found {Count} paired BLE device(s):", devices.Count);

        string keyword = _settings.TargetDevice.DeviceNameKeyword;
        string targetId = _settings.TargetDevice.PairedDeviceId;

        DeviceInformation? matchedDevice = null;

        foreach (DeviceInformation dev in devices)
        {
            bool isNameMatch = !string.IsNullOrWhiteSpace(keyword) &&
                               dev.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            bool isIdMatch = !string.IsNullOrWhiteSpace(targetId) &&
                             dev.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase);

            _logger.LogInformation(" - Device: \"{Name}\", ID: {Id}, Paired: {IsPaired}",
                dev.Name, dev.Id, dev.Pairing.IsPaired);

            if ((isIdMatch || isNameMatch) && matchedDevice == null)
            {
                matchedDevice = dev;
            }
        }

        if (matchedDevice != null)
        {
            _resolvedTargetDeviceId = matchedDevice.Id;
            _resolvedTargetDeviceName = matchedDevice.Name;

            _logger.LogInformation("Target paired iPhone detected: \"{Name}\" (ID: {Id})",
                _resolvedTargetDeviceName, _resolvedTargetDeviceId);

            TargetDeviceIdentified?.Invoke(this, new TargetDeviceInfoEventArgs(
                _resolvedTargetDeviceName, _resolvedTargetDeviceId, true));

            // Establish initial GATT connection session for Keep-Alive & ANCS
            _ = Task.Run(async () => await InitializeGattKeepAliveAsync(matchedDevice.Id));
        }
        else
        {
            _logger.LogWarning("No paired device matching Keyword: \"{Keyword}\" or ID: \"{Id}\" was found. " +
                               "Please pair your iPhone in Windows Bluetooth settings.", keyword, targetId);
        }
    }

    /// <summary>
    /// Handles incoming BLE advertisement packets, resolves temporary randomized MAC addresses,
    /// and extracts RSSI if the packet originates from the paired iPhone.
    /// </summary>
    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        ulong address = args.BluetoothAddress;
        short rawRssi = args.RawSignalStrengthInDBm;
        DateTime now = DateTime.UtcNow;

        // 1. Fast path: check memory cache for previously resolved address
        if (_addressCache.TryGetValue(address, out CachedResolution? cached))
        {
            TimeSpan cacheAge = now - cached.ResolvedAt;
            // iOS addresses rotate roughly every 15 minutes; purge cache after 20 minutes
            if (cacheAge.TotalMinutes < 20.0)
            {
                if (cached.IsTarget)
                {
                    cached.LastSeen = now;
                    ProcessMatchedRssi(rawRssi, now);
                }
                return;
            }
            // Expired cache entry, will re-resolve below
            _addressCache.TryRemove(address, out _);
        }

        // 2. Slow path: resolve address asynchronously via Windows IRK stack
        _ = Task.Run(async () => await ResolveAddressAndProcessAsync(address, rawRssi, now));
    }

    /// <summary>
    /// Uses Windows Bluetooth stack and IRK to resolve whether a random private address belongs
    /// to the target paired iPhone, caching the result.
    /// </summary>
    private async Task ResolveAddressAndProcessAsync(ulong address, short rawRssi, DateTime timestamp)
    {
        SemaphoreSlim sem = _resolvingSemaphores.GetOrAdd(address, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(100))
        {
            // Another thread is already resolving this address
            return;
        }

        try
        {
            // Re-check cache inside lock
            if (_addressCache.TryGetValue(address, out CachedResolution? existing) &&
                (timestamp - existing.ResolvedAt).TotalMinutes < 20.0)
            {
                if (existing.IsTarget)
                {
                    existing.LastSeen = timestamp;
                    ProcessMatchedRssi(rawRssi, timestamp);
                }
                return;
            }

            BluetoothLEDevice? device = null;
            try
            {
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Failed to resolve Bluetooth address 0x{Address:X12}", address);
            }

            if (device == null)
            {
                // Non-resolvable or transient packet; cache as negative with short expiration
                _addressCache[address] = new CachedResolution
                {
                    IsTarget = false,
                    ResolvedAt = timestamp,
                    LastSeen = timestamp
                };
                return;
            }

            bool isPaired = device.DeviceInformation.Pairing.IsPaired;
            string deviceId = device.DeviceId;
            string deviceName = device.Name;

            bool isTarget = false;

            if (isPaired)
            {
                if (!string.IsNullOrEmpty(_resolvedTargetDeviceId))
                {
                    isTarget = string.Equals(deviceId, _resolvedTargetDeviceId, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    string keyword = _settings.TargetDevice.DeviceNameKeyword;
                    isTarget = !string.IsNullOrWhiteSpace(keyword) &&
                               deviceName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                }
            }

            _addressCache[address] = new CachedResolution
            {
                IsTarget = isTarget,
                DeviceId = deviceId,
                DeviceName = deviceName,
                ResolvedAt = timestamp,
                LastSeen = timestamp
            };

            if (isTarget)
            {
                _logger.LogInformation("IRK match confirmed for address 0x{Address:X12} -> \"{Name}\" (ID: {Id})",
                    address, deviceName, deviceId);

                if (_activeTargetDevice == null || _activeTargetDevice.DeviceId != deviceId)
                {
                    _resolvedTargetDeviceId = deviceId;
                    _resolvedTargetDeviceName = deviceName;
                    AttachTargetDevice(device);
                }

                ProcessMatchedRssi(rawRssi, timestamp);
            }
            else
            {
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while resolving BLE address 0x{Address:X12}", address);
        }
        finally
        {
            sem.Release();
            _resolvingSemaphores.TryRemove(address, out _);
        }
    }

    /// <summary>
    /// Processes verified RSSI from target iPhone through the Kalman filter and State Machine.
    /// </summary>
    private void ProcessMatchedRssi(short rawRssi, DateTime timestamp)
    {
        double estimatedRssi = _kalmanFilter.Update(rawRssi, timestamp);
        _stateMachine.Evaluate(estimatedRssi, timestamp);

        RssiUpdated?.Invoke(this, new RssiUpdateEventArgs(rawRssi, estimatedRssi, timestamp));
    }

    /// <summary>
    /// Initializes GATT Keep-Alive connection to iPhone (Battery service / ANCS)
    /// to maintain BLE session even when iPhone screen is asleep.
    /// </summary>
    private async Task InitializeGattKeepAliveAsync(string deviceId)
    {
        try
        {
            _logger.LogInformation("Connecting to GATT Keep-Alive service for {DeviceId}...", deviceId);
            BluetoothLEDevice device = await BluetoothLEDevice.FromIdAsync(deviceId);
            if (device == null)
            {
                _logger.LogWarning("Unable to instantiate BluetoothLEDevice from ID: {DeviceId}", deviceId);
                return;
            }

            AttachTargetDevice(device);

            // Attempt to access Battery service (0x180F) or ANCS
            GattDeviceServicesResult servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status == GattCommunicationStatus.Success)
            {
                foreach (GattDeviceService s in servicesResult.Services)
                {
                    if (s.Uuid == BatteryServiceUuid || s.Uuid == AncsServiceUuid)
                    {
                        _keepAliveService = s;
                        _logger.LogInformation("GATT Keep-Alive service established (UUID: {Uuid})", s.Uuid);
                        break;
                    }
                }
            }
            else
            {
                _logger.LogDebug("GattServices returned status: {Status}", servicesResult.Status);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GATT Keep-Alive connection attempt completed with notice.");
        }
    }

    private void AttachTargetDevice(BluetoothLEDevice device)
    {
        if (_activeTargetDevice != null)
        {
            _activeTargetDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _activeTargetDevice.Dispose();
        }

        _activeTargetDevice = device;
        _activeTargetDevice.ConnectionStatusChanged += OnConnectionStatusChanged;

        bool isConnected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        _stateMachine.UpdateGattConnection(isConnected, DateTime.UtcNow);

        _logger.LogInformation("Target BLE device attached. Initial connection status: {Status}", device.ConnectionStatus);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        bool isConnected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        _logger.LogInformation("BLE ConnectionStatusChanged: {Status}", sender.ConnectionStatus);

        DateTime now = DateTime.UtcNow;
        _stateMachine.UpdateGattConnection(isConnected, now);

        if (!isConnected)
        {
            TargetDeviceConnectionLost?.Invoke(this, sender.DeviceId);
        }
    }

    private void OnSessionLockStateChanged(object? sender, bool isLocked)
    {
        if (isLocked)
        {
            _logger.LogInformation("Workstation session locked: suspending decay timer evaluations.");
        }
        else
        {
            _logger.LogInformation("Workstation session unlocked: resetting Kalman filter for clean re-acquisition.");
            _kalmanFilter.Reset();

            // Re-attempt GATT keep-alive reconnection if target was disconnected
            if (!string.IsNullOrEmpty(_resolvedTargetDeviceId) && !IsTargetConnected)
            {
                _ = Task.Run(async () => await InitializeGattKeepAliveAsync(_resolvedTargetDeviceId));
            }
        }
    }

    /// <summary>
    /// Periodic decay timer tick: if packets have stopped arriving for > SignalLossTimeoutMs,
    /// decays the estimated RSSI towards VirtualDecayRssi (-95 dBm) and updates State Machine.
    /// </summary>
    private void OnDecayTimerTick(object? state)
    {
        if (_disposed) return;
        if (_sessionMonitor.IsSessionLocked) return; // Never decay while workstation is already locked!

        DateTime now = DateTime.UtcNow;
        TimeSpan timeout = TimeSpan.FromMilliseconds(_settings.KalmanFilter.SignalLossTimeoutMs);

        if (_kalmanFilter.IsInitialized && (now - _kalmanFilter.LastMeasurementTime) >= timeout)
        {
            double decayedRssi = _kalmanFilter.ApplyDecay(now, timeout, _settings.KalmanFilter.VirtualDecayRssi);
            _stateMachine.Evaluate(decayedRssi, now);

            RssiUpdated?.Invoke(this, new RssiUpdateEventArgs(_kalmanFilter.LastRawRssi, decayedRssi, now));
        }
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        _logger.LogWarning("BluetoothLEAdvertisementWatcher stopped. Error: {Error}", args.Error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessionMonitor.SessionLockStateChanged -= OnSessionLockStateChanged;
        _decayTimer?.Dispose();

        if (_watcher != null)
        {
            _watcher.Received -= OnAdvertisementReceived;
            _watcher.Stopped -= OnWatcherStopped;
            try { _watcher.Stop(); } catch { }
        }

        if (_activeTargetDevice != null)
        {
            _activeTargetDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _activeTargetDevice.Dispose();
        }

        _keepAliveService?.Dispose();

        foreach (SemaphoreSlim sem in _resolvingSemaphores.Values)
        {
            sem.Dispose();
        }
        _resolvingSemaphores.Clear();
        _addressCache.Clear();

        _logger.LogInformation("BleProximityMonitor disposed.");
    }
}
