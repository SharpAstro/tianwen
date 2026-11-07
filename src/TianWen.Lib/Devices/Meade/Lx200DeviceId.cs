namespace TianWen.Lib.Devices.Meade;

/// <summary>
/// The ONE id an LX200-family mount (Meade, OnStep) gets, over serial and over the network alike (#2040): its product name
/// (<c>:GVP#</c>) and the <c>TW@</c> UUID the probe wrote into a spare site slot, else its product name and the transport it
/// was found on (the port, or the host). Never the firmware number (<c>:GVN#</c>), which an update changes, and never the site
/// names, which the user renames: both are evidence at most, and the firmware rides in the query
/// (<see cref="DeviceQueryKey.Firmware"/>).
/// </summary>
internal static class Lx200DeviceId
{
    /// <summary><c>LX85_&lt;uuid&gt;</c> with a UUID, <c>LX85_COM5</c> (or <c>On-Step_192.168.1.20</c>) without.</summary>
    public static string Of(string productName, string? uuid, string transport)
        => string.Join('_', SafeName(productName), uuid is { Length: > 0 } ? uuid : SafeName(transport));

    /// <summary>The characters a URI path or an id's <c>_</c> separator cannot carry, made <c>-</c>.</summary>
    public static string SafeName(string name) => name.Replace('_', '-').Replace('/', '-').Replace(':', '-');
}
