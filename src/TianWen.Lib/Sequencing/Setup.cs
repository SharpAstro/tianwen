using System;
using System.Collections.Generic;

namespace TianWen.Lib.Sequencing;

public record Setup(
    Mount Mount,
    Guider Guider,
    GuiderSetup GuiderFocuser,
    IReadOnlyList<OTA> Telescopes
) : IDisposable
{
    private bool disposedValue;

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