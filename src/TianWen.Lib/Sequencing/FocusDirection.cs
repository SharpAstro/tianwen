namespace TianWen.Lib.Sequencing;

/// <summary>
/// Describes the preferred approach direction for a focuser, accounting for the
/// physical-to-position mapping and gravity/mechanical preference.
/// <para>
/// <b>OutwardIsPositive</b>: whether increasing focuser position moves the drawtube outward (away from sensor).
/// <b>PreferOutward</b>: whether to approach best focus from the outward side (e.g., working against gravity
/// on an inward-hanging focuser means preferring outward approach so the final move is inward = with gravity).
/// </para>
/// <para>
/// The 2×2 matrix:
/// <list type="table">
///   <listheader><term>OutwardIsPositive</term><term>PreferOutward</term><term>Preferred sign</term><term>Example</term></listheader>
///   <item><term>true</term><term>true</term><term>+1</term><term>SCT: out=+, prefer outward approach</term></item>
///   <item><term>true</term><term>false</term><term>−1</term><term>Refractor: out=+, prefer inward (with gravity)</term></item>
///   <item><term>false</term><term>true</term><term>−1</term><term>Reversed focuser: out=−, prefer outward</term></item>
///   <item><term>false</term><term>false</term><term>+1</term><term>Reversed focuser: out=−, prefer inward</term></item>
/// </list>
/// </para>
/// </summary>
public readonly record struct FocusDirection(bool PreferOutward, bool OutwardIsPositive)
{
    /// <summary>
    /// Whether the preferred approach direction corresponds to increasing position values.
    /// True when <see cref="PreferOutward"/> and <see cref="OutwardIsPositive"/> agree.
    /// </summary>
    public bool PreferredDirectionIsPositive => PreferOutward == OutwardIsPositive;

    /// <summary>
    /// Sign multiplier for the preferred approach direction: +1 for increasing position, −1 for decreasing.
    /// Use this to determine overshoot direction in backlash compensation and scan direction in auto-focus.
    /// </summary>
    public int PreferredSign => PreferredDirectionIsPositive ? 1 : -1;
}
