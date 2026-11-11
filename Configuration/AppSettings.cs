using System.IO;
using System.Text.Json;

namespace ZeroTouchLock.Configuration;

public sealed class AppSettings
{
    public TargetDeviceConfig TargetDevice { get; set; } = new();
    public KalmanFilterConfig KalmanFilter { get; set; } = new();
    public ThresholdsConfig Thresholds { get; set; } = new();
    public FeaturesConfig Features { get; set; } = new();

    public static void SaveToFile(string filePath, AppSettings settings)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(filePath, json);
    }
}

public sealed class TargetDeviceConfig
{
    public string DeviceNameKeyword { get; set; } = "iPhone";
    public string PairedDeviceId { get; set; } = string.Empty;
}

public sealed class KalmanFilterConfig
{
    public double ProcessNoiseQ { get; set; } = 0.05;
    public double MeasurementNoiseR { get; set; } = 2.0;
    public int SignalLossTimeoutMs { get; set; } = 1500;
    public double VirtualDecayRssi { get; set; } = -95.0;
}

public sealed class ThresholdsConfig
{
    public double PresentRssiDbm { get; set; } = -68.0;
    public double WarningRssiDbm { get; set; } = -72.0;
    public double AwayRssiDbm { get; set; } = -78.0;
    public int WarningDurationMs { get; set; } = 2000;
    public int AwayDurationMs { get; set; } = 3500;
    public int UserInputGuardMs { get; set; } = 3000;
}

public sealed class FeaturesConfig
{
    public bool EnableStealthOverlay { get; set; } = true;
    public double OverlayOpacity { get; set; } = 0.75;
}
