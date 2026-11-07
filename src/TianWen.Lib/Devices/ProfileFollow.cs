using System;
using System.Collections.Generic;

namespace TianWen.Lib.Devices;

/// <summary>
/// Which of the identity ladder's lower rungs a profile may take when its device was not found under its id (#2040,
/// docs/architecture/device-architecture.md, "Step 3"), and what the selected profile followed in this pass.
/// <list type="bullet">
///   <item><see cref="ForSelected"/>: the selected profile takes the evidence rungs itself (the one candidate of its model,
///   or the one of several with its firmware) and records each follow, the id it held and the unit it took.</item>
///   <item><see cref="ForAnother"/>: every other profile follows ONLY an id the selected profile followed, to that same unit
///   (the owner, 2026-11-07): one unit, so a pointer moves for all who hold it. Neither the model nor the firmware moves
///   another rig's profile by itself, since the hardware found may not be that rig's. Reconcile the selected profile FIRST.</item>
///   <item><see cref="WithoutSelected"/>: no profile is selected in this pass, so nothing follows below the id.</item>
/// </list>
/// The machine-wide registry (#2047) makes this a real pointer; until then the follow is copied into each profile holding the id.
/// </summary>
public sealed class ProfileFollow
{
    private readonly Dictionary<string, Uri> _followed;

    private ProfileFollow(bool selected, Dictionary<string, Uri> followed)
    {
        IsSelected = selected;
        _followed = followed;
    }

    /// <summary>The selected profile's rule, with an empty record of what it follows.</summary>
    public static ProfileFollow ForSelected() => new(true, new(DeviceUriExtensions.DeviceKeyComparer));

    /// <summary>The rule for a pass with no selected profile: identity matches only.</summary>
    public static ProfileFollow WithoutSelected() => new(false, new(DeviceUriExtensions.DeviceKeyComparer));

    /// <summary>The rule for every other profile: follow what this (the selected) profile followed, nothing else.</summary>
    public ProfileFollow ForAnother() => new(false, _followed);

    /// <summary>Whether this profile takes the evidence rungs itself.</summary>
    public bool IsSelected { get; }

    /// <summary>Records that the selected profile followed <paramref name="heldId"/> to the discovered <paramref name="unit"/>.</summary>
    internal void Record(string heldId, Uri unit)
    {
        if (IsSelected)
        {
            _followed[heldId] = unit;
        }
    }

    /// <summary>The discovered unit the selected profile followed <paramref name="heldId"/> to in this pass, if it did.</summary>
    internal Uri? FollowedTo(string heldId) => _followed.TryGetValue(heldId, out var unit) ? unit : null;
}
