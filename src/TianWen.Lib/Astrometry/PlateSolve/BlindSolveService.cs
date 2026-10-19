using TianWen.Lib.Devices;

namespace TianWen.Lib.Astrometry.PlateSolve;

/// <summary>
/// The online service a blind solve of last resort goes to, and where the user's key for it is kept: one name for the solver
/// that reads the key and every host that lets the user set it (the node's <c>/api/v1/platesolve/blind/key</c>, the GUI's
/// Equipment tab, the CLI).
/// </summary>
public static class BlindSolveService
{
    /// <summary>The service, as the user knows it.</summary>
    public const string Name = "nova.astrometry.net";

    /// <summary>Where the user gets a key for it.</summary>
    public const string KeyPage = "https://nova.astrometry.net/api_help";

    /// <summary>The credential store's key for the user's API key: per user, shared by every profile, never in a profile or a URI.</summary>
    public static string CredentialKey { get; } = ICredentialStore.KeyFor(Name, "apiKey");
}
