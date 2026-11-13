using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// A session that cannot be built from its profile, refused by <see cref="ISessionFactory.Create"/> before
/// any device is connected or cooled. The message is written for the OBSERVER and names the fix, so every
/// start shows it verbatim: the GUI's notification feed, the node's answer (a client error, never a 500) and
/// the CLI. An <see cref="ArgumentException"/>, since the profile the caller named is what is wrong, but
/// built without a parameter name, which would otherwise append "(Parameter '...')" to the words.
/// </summary>
public sealed class SessionRefusedException(string userMessage) : ArgumentException(userMessage)
{
    /// <summary>
    /// A session always has a guider (#989): TianWen ships its own, so an unguided session is not supported.
    /// </summary>
    public const string NoGuiderMessage =
        "This profile has no guider. A session needs one: choose a guider in Equipment (the built-in guider works with any guide camera).";

    /// <summary>
    /// A session points a mount. A profile with a site and no mount plans a night (#2113) and cannot run one.
    /// </summary>
    public const string NoMountMessage =
        "This profile has no mount. A session needs one: choose a mount in Equipment.";
}
