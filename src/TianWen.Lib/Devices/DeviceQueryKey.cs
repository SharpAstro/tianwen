namespace TianWen.Lib.Devices;

public enum DeviceQueryKey
{
    Latitude,
    Longitude,
    Port,
    Baud,
    Gain,
    Offset,
    Host,
    DeviceNumber,
}

public static class DeviceQueryKeyExtensions
{
    extension(DeviceQueryKey key)
    {
        public string Key => key switch
        {
            DeviceQueryKey.Latitude => "latitude",
            DeviceQueryKey.Longitude => "longitude",
            DeviceQueryKey.Port => "port",
            DeviceQueryKey.Baud => "baud",
            DeviceQueryKey.Gain => "gain",
            DeviceQueryKey.Offset => "offset",
            DeviceQueryKey.Host => "host",
            DeviceQueryKey.DeviceNumber => "deviceNumber",
            _ => key.ToString().ToLowerInvariant()
        };
    }
}
