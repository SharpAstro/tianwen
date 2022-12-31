using System;

namespace Astap.Lib.Sequencing;

public class Telescope : IDisposable
{
    private bool disposedValue;

    public Telescope(
        string name,
        int focalLength,
        Camera camera,
        Cover? cover,
        Focuser? focuser,
        FocusDirection focusDirection,
        FilterWheel? filterWheel,
        Switch? switches
    )
    {
        Name = name;
        FocalLength = focalLength;
        Camera = camera;
        Cover = cover;
        Focuser = focuser;
        FilterWheel = filterWheel;
        Switches = switches;
    }

    public string Name { get; }

    public int FocalLength { get; }

    public Camera Camera { get; }

    public Cover? Cover { get; }

    public Focuser? Focuser { get; }

    public FilterWheel? FilterWheel { get; }

    public Switch? Switches { get; }

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
