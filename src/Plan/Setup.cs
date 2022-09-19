using Astap.Lib.Devices;
using System;
using System.Collections.Generic;

namespace Astap.Lib.Plan;

public class Setup : IDisposable
{
    private readonly List<Telescope> _telescopes;
    private bool disposedValue;

    public Setup(
        Mount mount,
        Guider guider,
        Telescope telescope,
        params Telescope[] telescopes)
    {
        Mount = mount;
        Guider = guider;
        _telescopes = new(telescopes.Length + 1)
        {
            telescope
        };
        _telescopes.AddRange(telescopes);
    }

    public Mount Mount { get; }

    public Guider Guider { get; }

    public ICollection<Telescope> Telescopes { get { return _telescopes; } }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                Mount.Dispose();
                Guider.Dispose();
                foreach (var telescope in _telescopes)
                {
                    telescope.Dispose();
                }
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~Setup()
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
