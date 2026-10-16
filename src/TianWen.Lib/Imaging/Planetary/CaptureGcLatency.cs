using System;
using System.Runtime;
using System.Threading;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The process's GC latency mode while a planetary capture runs (TIA2's capture-priority leg):
/// <see cref="GCLatencyMode.SustainedLowLatency"/>, which keeps blocking full collections away while it is held, and the
/// mode found before the first capture took it put back as the last one ends.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> Through the node, with its live stack and view, a capture under 32 normal-priority spinners saw 3 to 9
/// gen 2 collections a minute pausing 283 to 592 ms in all, against 0 to 4 and 11 to 77 ms in process; a pause holds every
/// managed thread, the capture's reader and its recording's writer included, and its suspension waits for every starved
/// normal-priority thread to reach a safe point. A pause on top of a backlog passed the driver's 64-frame hand-off.</para>
/// <para><b>Process-wide, so held only while a capture runs</b> and counted, since two captures may run at once (two OTAs):
/// the node serves everything else too.</para>
/// </remarks>
internal sealed class CaptureGcLatency(Func<GCLatencyMode> get, Action<GCLatencyMode> set)
{
    /// <summary>The process's own, through <see cref="GCSettings.LatencyMode"/>.</summary>
    public static CaptureGcLatency Process { get; } = new CaptureGcLatency(static () => GCSettings.LatencyMode, static mode => GCSettings.LatencyMode = mode);

    // A lock, not an Interlocked count: the count and the mode found must change together, or a capture ending as another
    // starts could put back the wrong mode. Taken once as a capture's loop starts and once as it ends, never per frame and
    // never from a render thread.
    private readonly Lock _gate = new Lock();
    private int _holders;
    private GCLatencyMode _found;

    /// <summary>The captures holding the mode now.</summary>
    internal int Holders
    {
        get
        {
            lock (_gate)
            {
                return _holders;
            }
        }
    }

    /// <summary>Holds <see cref="GCLatencyMode.SustainedLowLatency"/> until the returned hold is disposed.</summary>
    public IDisposable Hold()
    {
        lock (_gate)
        {
            if (_holders++ == 0)
            {
                _found = get();
                set(GCLatencyMode.SustainedLowLatency);
            }
        }
        return new Holding(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_holders == 0)
            {
                set(_found);
            }
        }
    }

    private sealed class Holding(CaptureGcLatency owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
