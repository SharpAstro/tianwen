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
    FocuserBacklashIn,
    FocuserBacklashOut,
    ApiKey,
}

public static class DeviceQueryKeyExtensions
{
    extension(DeviceQueryKey key)
    {
        /// <summary>
        /// True if this key carries transport-level state (how to reach the device)
        /// rather than user-configured preferences. Transport params should be refreshed
        /// from discovery on reconcile (COM port swap, new DHCP lease, etc.) while
        /// user-config params must be preserved to avoid clobbering user edits like
        /// site coordinates or filter wheel slot names.
        /// </summary>
        public bool IsTransport => key switch
        {
            DeviceQueryKey.Port => true,
            DeviceQueryKey.Baud => true,
            DeviceQueryKey.Host => true,
            DeviceQueryKey.DeviceNumber => true,
            _ => false
        };

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
            DeviceQueryKey.FocuserBacklashIn => "focuserBacklashIn",
            DeviceQueryKey.FocuserBacklashOut => "focuserBacklashOut",
            DeviceQueryKey.ApiKey => "apiKey",
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

    /// <summary>
    /// Classify a raw query key string as transport-level. Used by URI reconcile
    /// when the <see cref="DeviceQueryKey"/> enum doesn't have a matching entry
    /// (e.g. dynamic filter slot keys "filter3" default to user-config).
    /// </summary>
    public static bool IsTransportKey(string rawKey)
    {
        // Case-insensitive match against the Key strings of transport enum members.
        foreach (DeviceQueryKey k in Enum.GetValues<DeviceQueryKey>())
        {
            if (k.IsTransport && string.Equals(k.Key, rawKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    extension(NameValueCollection query)
    {
        public string? QueryValue(DeviceQueryKey key) => query[key.Key];
    }

    extension(Uri uri)
    {
        public string? QueryValue(DeviceQueryKey key) => HttpUtility.ParseQueryString(uri.Query)[key.Key];
    }
}
