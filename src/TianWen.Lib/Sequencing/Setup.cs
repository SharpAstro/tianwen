using System;
using System.Collections.Generic;

namespace Astap.Lib.Sequencing;

public record Setup(
    Mount Mount,
    Guider Guider,
    GuiderFocuser GuiderFocuser,
    IReadOnlyList<Telescope> Telescopes
) : IDisposable
{
    private bool disposedValue;

    public Setup(Mount mount, Guider guider, GuiderFocuser guiderFocuser, Telescope primary, params Telescope[] secondaries)
        : this(mount, guider, guiderFocuser, [primary, .. secondaries])
    {
        // calls primary constructor
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                Mount.Dispose();
                Guider.Dispose();
                GuiderFocuser.Focuser?.Dispose();
                foreach (var telescope in Telescopes)
                {
                    telescope.Dispose();
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