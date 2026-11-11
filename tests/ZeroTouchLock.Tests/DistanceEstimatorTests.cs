using Xunit;
using ZeroTouchLock.Core;

namespace ZeroTouchLock.Tests;

public class DistanceEstimatorTests
{
    [Fact]
    public void TxPowerRssi_YieldsExactlyOneMeter()
    {
        // When RSSI == TxPower (-59.0 dBm), distance d = 10^0 = 1.0 meter
        double distance = DistanceEstimator.EstimateDistanceMeters(-59.0, txPower: -59.0, pathLossExponent: 2.2);

        Assert.Equal(1.0, distance, precision: 3);
    }

    [Fact]
    public void AttenuatedRssi_CalculatesCorrectLogDistance()
    {
        // When (TxPower - RSSI) = 22.0 dBm (i.e. RSSI = -81.0 dBm)
        // exponent = 22 / (10 * 2.2) = 1.0 => d = 10^1 = 10.0 meters
        double distance = DistanceEstimator.EstimateDistanceMeters(-81.0, txPower: -59.0, pathLossExponent: 2.2);

        Assert.Equal(10.0, distance, precision: 2);
    }

    [Fact]
    public void StrongSignal_ClampsToMinimumDistance()
    {
        // Unusually strong RSSI or positive values should be safely clamped to 0.1m
        double distance = DistanceEstimator.EstimateDistanceMeters(0.0);
        Assert.Equal(0.1, distance);

        double distNegative = DistanceEstimator.EstimateDistanceMeters(-30.0);
        Assert.True(distNegative > 0.05 && distNegative < 0.5);
    }

    [Theory]
    [InlineData(0.85, "approx. 85 cm")]
    [InlineData(1.42, "approx. 1.4 m")]
    [InlineData(3.12, "approx. 3.1 m")]
    public void FormatDistance_FormatsHumanReadableStrings(double meters, string expected)
    {
        string formatted = DistanceEstimator.FormatDistance(meters);
        Assert.Equal(expected, formatted);
    }
}
