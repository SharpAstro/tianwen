using System;

namespace Astap.Lib.Sequencing;

public record Telescope(
    string Name,
    int FocalLength,
    Camera Camera,
    Cover? Cover,
    Focuser? Focuser,
    FocusDirection FocusDirection,
    FilterWheel? FilterWheel,
    Switch? Switches
    ) : IDisposable
{
    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                Camera.Dispose();
                Cover?.Dispose();
                Focuser?.Dispose();
                FilterWheel?.Dispose();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~Telescope()
    // {
    //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}