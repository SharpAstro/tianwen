using System;

namespace TianWen.Cli;

/// <summary>
/// The one reading of a <c>--black-point-sigma</c> value (#2309), for the two commands that take one:
/// <c>dataset degrade --mode stars</c>, which writes an export stretched from that depth, and
/// <c>image remove-stars --model</c>, which runs a model at it for a measurement.
/// </summary>
internal static class BlackPointOption
{
    public const string Name = "--black-point-sigma";

    /// <summary>
    /// Why <paramref name="depth"/> cannot be taken as given, or <see langword="null"/> when it can, or when none was
    /// given. A depth is a number of sky-noise sigma over 0. <paramref name="applies"/> says whether the command's
    /// other options give the depth something to act on, and <paramref name="needs"/> is what is said when they do not.
    /// </summary>
    public static string? Refusal(double? depth, bool applies, string needs)
    {
        if (depth is not { } given)
        {
            return null;
        }
        if (!(double.IsFinite(given) && given > 0))
        {
            return FormattableString.Invariant($"{Name} {given}: give a number of sigma over 0");
        }
        return applies ? null : needs;
    }
}
