namespace TianWen.Lib.Imaging;

public record struct StretchParameters(double Factor, double ShadowsClipping)
{
    /// <summary>
    /// The auto-stretch every consumer starts from: an INSPECTION stretch, matching N.I.N.A.'s
    /// per-light preview (<c>autoStretchFactor = 0.2</c>, <c>blackClipping = -2.8</c>) and close to
    /// PixInsight's STF (0.25 / -2.8).
    /// </summary>
    /// <remarks>
    /// <para><b>It used to be (0.1, -5.0), which was never a decision.</b> The pair arrived in one
    /// broad commit with no rationale recorded anywhere, and the repo's own write-up already cited the
    /// reference values it disagreed with (<c>docs/plans/narrowband-colour.md</c>: "MAD x 1.4826,
    /// shadow clipping at -2.8 sigma, target background 0.25"). A test comment claiming -3 "matches the
    /// production default" had drifted from it too.</para>
    /// <para><b>The cost was contrast on faint signal, not merely brightness.</b> The midtones
    /// transfer function maps the median to <see cref="Factor"/> by construction, so the sky landed at
    /// 0.1 against N.I.N.A.'s 0.2 -- but the part that mattered is what happens just above it. Measured
    /// on a real 120 s sub of the Sculptor Galaxy (median 0.0520, MAD 0.0033), a pixel 3 MAD above the
    /// sky rendered 0.036 above the background at (0.1, -5.0) and 0.103 at (0.2, -2.8): nearly three
    /// times the separation, on exactly the faint structure a preview exists to show. Even the
    /// viewer's 150% Boost could not close it, reaching 0.150 against a 0.200 background.</para>
    /// <para><b>This is an INSPECTION stretch, and a master eventually wants its own.</b> The question
    /// a single sub has to answer is "is the target there, is it focused, did cloud roll in", which
    /// wants an aggressive curve. A deep, background-extracted master rendered at 0.2 reads milky --
    /// and the STF already steepens itself on a master, whose MAD is small against its signal. That
    /// belongs at the call site, where <see cref="StretchMode"/> is already chosen deliberately per
    /// consumer (see <c>MasterPreviewRenderer</c>), NOT inferred from the pixels: an auto-stretch that
    /// moves on its own when a frame "looks flattened" re-renders the moment a frame is enhanced.</para>
    /// </remarks>
    public static StretchParameters Default => Presets[0];

    /// <summary>
    /// What the stretch-parameter button cycles through, in order. <b>The first entry IS
    /// <see cref="Default"/></b>, which is derived from it rather than stated twice -- the same shape
    /// <c>ViewerActions.DefaultStretchMode</c> takes over <c>StretchLinkModes</c>.
    /// </summary>
    /// <remarks>
    /// That relation is load-bearing, not tidiness. <c>ViewerActions.CycleStretchPreset</c> walks a
    /// separate <c>StretchPresetIndex</c> that starts at zero and is never reconciled against the
    /// parameters in hand, so a default which is not <c>Presets[0]</c> is a default the cycler steps
    /// off on its first press and can never return to. It held before only because the old default
    /// happened to equal the first entry; deriving it means a future change to either cannot break the
    /// other silently, and <c>StretchDefaultInspectionTests</c> pins it besides.
    /// </remarks>
    public static readonly StretchParameters[] Presets =
    [
        new(0.2, -2.8),   // N.I.N.A.'s per-light preview, and this is the default
        new(0.25, -2.8),  // PixInsight's STF
        new(0.15, -2.8),
        new(0.1, -2.8),
        new(0.2, -5.0),
        new(0.15, -5.0),
        new(0.1, -5.0),   // the default before 2026-09-14; kept, because a deep clip suits some frames
        new(0.25, -5.0),
    ];

    public override readonly string ToString() => $"({Factor}, {-ShadowsClipping})";
}
