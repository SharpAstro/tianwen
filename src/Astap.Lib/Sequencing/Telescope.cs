using System;

namespace Astap.Lib.Sequencing;

public class Telescope(
    string name,
    int focalLength,
    Camera camera,
    Cover? cover,
    Focuser? focuser,
    FocusDirection focusDirection,
    FilterWheel? filterWheel,
    Switch? switches
    ) : IDisposable
{
    private bool disposedValue;

    public string Name { get; } = name;

    public int FocalLength { get; } = focalLength;

    public Camera Camera { get; } = camera;

    public Cover? Cover { get; } = cover;

    public Focuser? Focuser { get; } = focuser;

    public FilterWheel? FilterWheel { get; } = filterWheel;

    public Switch? Switches { get; } = switches;

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
