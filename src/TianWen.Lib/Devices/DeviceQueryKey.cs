using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Web;

namespace TianWen.Lib.Devices;

public enum DeviceQueryKey
{
    Latitude,
    Longitude,
    Elevation,
    Port,
    Baud,
    Gain,
    Offset,
    Host,
    DeviceNumber,
    Data,
    PulseGuideSource,
    ReverseDecAfterFlip,
    UseNeuralGuider,
    NeuralBlendFactor,
    PePeriodSeconds,
    PePeakTopeakArcsec,
    ReuseCalibration,
    FocuserInitialPosition,
    FocuserBestFocus,
}

public static class DeviceQueryKeyExtensions
{
    extension(DeviceQueryKey key)
    {
        public string Key => key switch
        {
            DeviceQueryKey.Latitude => "latitude",
            DeviceQueryKey.Longitude => "longitude",
            DeviceQueryKey.Elevation => "elevation",
            DeviceQueryKey.Port => "port",
            DeviceQueryKey.Baud => "baud",
            DeviceQueryKey.Gain => "gain",
            DeviceQueryKey.Offset => "offset",
            DeviceQueryKey.Host => "host",
            DeviceQueryKey.DeviceNumber => "deviceNumber",
            DeviceQueryKey.Data => "data",
            DeviceQueryKey.PulseGuideSource => "pulseGuideSource",
            DeviceQueryKey.ReverseDecAfterFlip => "reverseDecAfterFlip",
            DeviceQueryKey.UseNeuralGuider => "useNeuralGuider",
            DeviceQueryKey.NeuralBlendFactor => "neuralBlendFactor",
            DeviceQueryKey.PePeriodSeconds => "pePeriodSeconds",
            DeviceQueryKey.PePeakTopeakArcsec => "pePeakTopeakArcsec",
            DeviceQueryKey.ReuseCalibration => "reuseCalibration",
            DeviceQueryKey.FocuserInitialPosition => "focuserInitialPosition",
            DeviceQueryKey.FocuserBestFocus => "focuserBestFocus",
            _ => key.ToString().ToLowerInvariant()
        };
    }

    /// <summary>
    /// Query key for a named filter slot (1-based), e.g. "filter1", "filter2".
    /// </summary>
    public static string FilterKey(int slotNumber) => $"filter{slotNumber.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Query key for a filter's focus offset (1-based), e.g. "offset1", "offset2".
    /// Distinct from <see cref="DeviceQueryKey.Offset"/> which is the camera ADC offset.
    /// </summary>
    public static string FilterOffsetKey(int slotNumber) => $"offset{slotNumber.ToString(CultureInfo.InvariantCulture)}";

    extension(NameValueCollection query)
    {
        public string? QueryValue(DeviceQueryKey key) => query[key.Key];
    }

    extension(Uri uri)
    {
        public string? QueryValue(DeviceQueryKey key) => HttpUtility.ParseQueryString(uri.Query)[key.Key];
    }
}
