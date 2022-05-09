using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using static Astap.Lib.AsomHelper;

namespace Astap.Lib;

public class AscomProfile : IDisposable
{
    private readonly dynamic _profile;
    private bool disposedValue;

    public AscomProfile() => _profile = NewComObject("ASCOM.Utilities.Profile");

    public IEnumerable<string> RegisteredDeviceTypes => EnumerateProperty<string>(_profile?.RegisteredDeviceTypes);

    public IEnumerable<(string progId, string displayName)> RegisteredDevices(string deviceType) => EnumerateKeyValueProperty(_profile?.RegisteredDevices(deviceType));

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                if (_profile is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
