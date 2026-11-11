namespace ZeroTouchLock.Core;

/// <summary>
/// 1-Dimensional Kalman Filter designed for smoothing volatile Bluetooth Low Energy (BLE) RSSI signals.
/// Includes an integrated signal loss decay mechanism that gradually penalizes the estimated RSSI
/// when advertisement packets are lost for more than a specified timeout.
/// </summary>
public sealed class KalmanFilter1D
{
    private double _x; // State estimate (estimated RSSI in dBm)
    private double _p; // Estimation error covariance
    private double _q; // Process noise covariance
    private double _r; // Measurement noise covariance
    private double _lastRawRssi;
    private DateTime _lastMeasurementTime = DateTime.MinValue;
    private bool _isInitialized;
    private readonly object _syncLock = new();

    public double EstimatedRssi
    {
        get
        {
            lock (_syncLock) return _x;
        }
    }

    public double LastRawRssi
    {
        get
        {
            lock (_syncLock) return _lastRawRssi;
        }
    }

    public bool IsInitialized
    {
        get
        {
            lock (_syncLock) return _isInitialized;
        }
    }

    public DateTime LastMeasurementTime
    {
        get
        {
            lock (_syncLock) return _lastMeasurementTime;
        }
    }

    public double ProcessNoiseQ
    {
        get => _q;
        set => _q = Math.Max(0.0001, value);
    }

    public double MeasurementNoiseR
    {
        get => _r;
        set => _r = Math.Max(0.0001, value);
    }

    /// <summary>
    /// Initializes the Kalman filter with process and measurement noise covariances.
    /// </summary>
    /// <param name="processNoiseQ">Process noise covariance Q (default 0.05)</param>
    /// <param name="measurementNoiseR">Measurement noise covariance R (default 2.0)</param>
    public KalmanFilter1D(double processNoiseQ = 0.05, double measurementNoiseR = 2.0)
    {
        _q = processNoiseQ;
        _r = measurementNoiseR;
        Reset();
    }

    /// <summary>
    /// Updates the Kalman filter with a new raw RSSI observation.
    /// </summary>
    /// <param name="rawRssi">The newly observed RSSI in dBm.</param>
    /// <param name="timestamp">Timestamp of the observation.</param>
    /// <returns>Smoothed estimate of the RSSI.</returns>
    public double Update(double rawRssi, DateTime timestamp)
    {
        lock (_syncLock)
        {
            _lastRawRssi = rawRssi;
            _lastMeasurementTime = timestamp;

            if (!_isInitialized)
            {
                _x = rawRssi;
                _p = 1.0;
                _isInitialized = true;
                return _x;
            }

            // 1. Prediction step
            // x_pri = x_est (constant position assumption)
            double xPri = _x;
            // p_pri = p_est + Q
            double pPri = _p + _q;

            // 2. Measurement update step
            // Kalman Gain: K = p_pri / (p_pri + R)
            double k = pPri / (pPri + _r);

            // Updated estimate: x_est = x_pri + K * (z - x_pri)
            _x = xPri + k * (rawRssi - xPri);

            // Updated covariance: p_est = (1 - K) * p_pri
            _p = (1.0 - k) * pPri;

            return _x;
        }
    }

    /// <summary>
    /// Checks if packet loss exceeds the timeout threshold. If timed out, feeds a virtual
    /// penalty measurement (e.g. -95.0 dBm or -100.0 dBm) to smoothly decay the estimated signal.
    /// </summary>
    /// <param name="now">Current time.</param>
    /// <param name="timeout">Duration without packets before decay begins (e.g. 1500ms).</param>
    /// <param name="virtualDecayRssi">Virtual penalty RSSI (e.g. -95.0 dBm or -100.0 dBm).</param>
    /// <returns>Current estimated RSSI after decay evaluation.</returns>
    public double ApplyDecay(DateTime now, TimeSpan timeout, double virtualDecayRssi)
    {
        lock (_syncLock)
        {
            if (!_isInitialized)
                return _x;

            TimeSpan elapsed = now - _lastMeasurementTime;
            if (elapsed >= timeout)
            {
                // Prediction step
                double xPri = _x;
                double pPri = _p + _q;

                // Virtual decay step using penalty measurement
                double decayNoise = _r * 1.5;
                double k = pPri / (pPri + decayNoise);

                _x = xPri + k * (virtualDecayRssi - xPri);
                _p = (1.0 - k) * pPri;

                // Advance last measurement time partially to throttle repeated decay steps
                _lastMeasurementTime = now - (timeout - TimeSpan.FromMilliseconds(400));
            }

            return _x;
        }
    }

    /// <summary>
    /// Resets the filter to uninitialized state or to a specified seed value.
    /// </summary>
    public void Reset(double? initialValue = null)
    {
        lock (_syncLock)
        {
            if (initialValue.HasValue)
            {
                _x = initialValue.Value;
                _p = 1.0;
                _isInitialized = true;
            }
            else
            {
                _x = -100.0;
                _p = 1.0;
                _isInitialized = false;
            }
            _lastRawRssi = _x;
            _lastMeasurementTime = DateTime.MinValue;
        }
    }
}
