using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Devices.Canon;

/// <summary>
/// Which serial the Canon body at each WPD path has, as far as this process has found out (#1097).
/// <para>
/// The path is where the cable is plugged in, so it changes when the camera moves, while the serial is the body's own.
/// Discovery keys a camera by its serial, and only the camera can say it: a read that opens the device and can cost another
/// program's live view a frame. So an answer is kept here, from every read and from every driver that connects (its session
/// already reports the serial), and a path in the map is never read again. That also keeps discovery from opening a camera
/// one of this process's drivers is holding, since the driver recorded the serial when it connected.
/// </para>
/// An empty serial is an answer too: the body reported none, and asking again would get the same. One instance per process
/// (a singleton), shared by the source and the drivers.
/// </summary>
internal sealed class CanonBodyRegistry
{
    private readonly ConcurrentDictionary<string, string> _serials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What is known of the body at <paramref name="wpdId"/>: its serial, or empty when it reports none.</summary>
    public bool TryGetSerial(string wpdId, [NotNullWhen(true)] out string? serial) => _serials.TryGetValue(wpdId, out serial);

    /// <summary>Records that the body at <paramref name="wpdId"/> has <paramref name="serial"/>, empty when it reports none.</summary>
    public void Remember(string wpdId, string serial) => _serials[wpdId] = serial;
}
