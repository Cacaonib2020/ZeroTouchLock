using Xunit;
using ZeroTouchLock.Core;

namespace ZeroTouchLock.Tests;

public class KalmanFilterTests
{
    [Fact]
    public void InitialObservation_SetsEstimateDirectly()
    {
        var filter = new KalmanFilter1D(processNoiseQ: 0.05, measurementNoiseR: 2.0);
        DateTime now = DateTime.UtcNow;

        double estimate = filter.Update(-65.0, now);

        Assert.True(filter.IsInitialized);
        Assert.Equal(-65.0, estimate);
        Assert.Equal(-65.0, filter.EstimatedRssi);
    }

    [Fact]
    public void NoisyObservations_AreSmoothedByFilter()
    {
        var filter = new KalmanFilter1D(processNoiseQ: 0.05, measurementNoiseR: 2.0);
        DateTime now = DateTime.UtcNow;

        // Establish steady state at -60 dBm
        for (int i = 0; i < 10; i++)
        {
            filter.Update(-60.0, now.AddMilliseconds(i * 100));
        }

        // Sudden transient spike to -85 dBm (due to multipath interference)
        double smoothedSpike = filter.Update(-85.0, now.AddMilliseconds(1100));

        // The filtered value must attenuate the -25 dBm jump significantly!
        Assert.True(smoothedSpike > -75.0, $"Filtered spike was {smoothedSpike}, expected > -75.0 dBm");
        Assert.True(smoothedSpike < -60.0, $"Filtered spike was {smoothedSpike}, expected < -60.0 dBm");
    }

    [Fact]
    public void PacketLoss_AppliesVirtualDecayTowardsPenaltyRssi()
    {
        var filter = new KalmanFilter1D(processNoiseQ: 0.05, measurementNoiseR: 2.0);
        DateTime start = DateTime.UtcNow;

        // Initialized at strong signal -60 dBm
        filter.Update(-60.0, start);

        // 2000 ms elapsed with no packets (exceeding 1500ms timeout)
        DateTime later = start.AddMilliseconds(2000);
        double decayed = filter.ApplyDecay(later, TimeSpan.FromMilliseconds(1500), -95.0);

        // Signal should have decayed downwards towards -95 dBm
        Assert.True(decayed < -60.0, $"Decayed RSSI was {decayed}, expected < -60.0");
    }
}
