namespace ZeroTouchLock.Core;

/// <summary>
/// Estimates distance in meters from Bluetooth Low Energy (BLE) RSSI
/// using the Log-Distance Path Loss Model:
/// d = 10 ^ ((TxPower - RSSI) / (10 * n))
/// </summary>
public static class DistanceEstimator
{
    public const double DefaultTxPower = -59.0;       // Expected RSSI at 1 meter for iPhone
    public const double DefaultPathLossExponent = 2.2; // Indoor office/room attenuation factor (2.0 ~ 2.5)

    /// <summary>
    /// Calculates estimated distance in meters based on smoothed RSSI.
    /// Clamps distance reasonably between 0.1m and 50.0m to avoid unrealistic values.
    /// </summary>
    /// <param name="rssiDbm">Kalman filtered RSSI in dBm</param>
    /// <param name="txPower">Reference RSSI at 1 meter (default -59 dBm)</param>
    /// <param name="pathLossExponent">Path loss exponent n (default 2.2)</param>
    /// <returns>Estimated distance in meters.</returns>
    public static double EstimateDistanceMeters(double rssiDbm, double txPower = DefaultTxPower, double pathLossExponent = DefaultPathLossExponent)
    {
        if (rssiDbm >= 0)
        {
            return 0.1;
        }

        double exponent = (txPower - rssiDbm) / (10.0 * pathLossExponent);
        double distance = Math.Pow(10.0, exponent);

        return Math.Clamp(distance, 0.1, 50.0);
    }

    /// <summary>
    /// Formats the estimated distance for UI display (e.g. "approx. 1.4 m").
    /// </summary>
    public static string FormatDistance(double distanceMeters)
    {
        if (distanceMeters < 1.0)
        {
            double cm = Math.Round(distanceMeters * 100, MidpointRounding.AwayFromZero);
            return $"approx. {cm:F0} cm";
        }
        double m = Math.Round(distanceMeters, 1, MidpointRounding.AwayFromZero);
        return $"approx. {m:F1} m";
    }
}
