using System;
using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Devices;

public interface IDeviceUriRegistry
{
    bool TryGetDeviceFromUri(Uri uri, [NotNullWhen(true)] out DeviceBase? device);
}