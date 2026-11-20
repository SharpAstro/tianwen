using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Geometry;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>A step the rule applied: the rectangle before and after it.</summary>
public readonly record struct MasterAutoCropApplied(PixelRect Before, PixelRect After);

/// <summary>
/// What one auto-crop scan found: the rectangle, which of the two tiers answered, and whether the
/// walk refused an edge. All three are needed at the status bar, because a crop is a border that is
/// missing and the only other evidence is the file looking different.
/// </summary>
/// <param name="Rect">The area to keep, in full-frame pixels, after the rule was applied.</param>
/// <param name="FromCoverage">True when the master's own coverage plane answered (exact), false when
/// the edge-noise walk did (an estimate).</param>
/// <param name="Declined">True when at least one edge refused: its noise was still falling at the
/// bound, so that edge was left alone by the walk. Only meaningful for the walk.</param>
/// <param name="Trims">The walk's four verdicts, or null where it did not run (the coverage plane
/// answered, or the union was empty). A caller that wants to act on a REFUSAL needs to know WHICH
/// edge refused, which the aggregate <paramref name="Declined"/> cannot say: keeping a refused
/// edge is right for a viewer, and wrong where the crop feeds a background model.</param>
/// <param name="DeclinedTrim">What the rule's policy took off the declined edges (before and after),
/// or null when it took nothing.</param>
/// <param name="Margin">What the rule's margin took off every edge (before and after), or null when it
/// was zero or ignored.</param>
/// <param name="MarginIgnored">True when a margin was asked for and would have left nothing, so was ignored.</param>
/// <param name="DeclinedTrimKept">True when trimming the declined edges would have left nothing, so they were kept.</param>
public readonly record struct MasterAutoCropResult(
    PixelRect Rect,
    bool FromCoverage,
    bool Declined,
    CoverageEdgeTrims? Trims = null,
    MasterAutoCropApplied? DeclinedTrim = null,
    MasterAutoCropApplied? Margin = null,
    bool MarginIgnored = false,
    bool DeclinedTrimKept = false);

/// <summary>
/// The crop rule: what a refused edge loses, and an optional margin. One rule per kind of consumer,
/// stated by the caller, never implied.
/// </summary>
/// <param name="Policy">What the walk's refused edges lose. <see cref="CoverageTrimPolicy.KeepEveryPixel"/>
/// is for a person looking (every pixel that exists); the producing policy with a
/// <see cref="CoverageTrimPolicy.DeclinedFraction"/> is for a model.</param>
/// <param name="Margin">Fraction of each axis inset from every edge after the scan, clamped to 0 to 0.25.
/// An inset that would leave 16 px or less on either axis is ignored.</param>
public sealed record MasterAutoCropRule(CoverageTrimPolicy Policy, double Margin = 0.0)
{
    /// <summary>The viewer's rule: a refused edge is left alone and there is no margin.</summary>
    public static MasterAutoCropRule ForViewing { get; } = new(CoverageTrimPolicy.KeepEveryPixel);

    /// <summary>The rule for a crop that feeds a model: the producing policy with its declined fraction
    /// clamped to 0 to 0.25 (0 keeps a refused edge, as the viewer does), and the margin.</summary>
    public static MasterAutoCropRule ForModel(double declinedFraction, double margin = 0.0) =>
        new(declinedFraction > 0
            ? CoverageTrimPolicy.Default with { DeclinedFraction = Math.Clamp(declinedFraction, 0.0, 0.25) }
            : CoverageTrimPolicy.KeepEveryPixel, margin);
}

/// <summary>
/// Finds the area of a stacked master worth keeping, as ONE routine for the viewer and
/// <c>image autocrop</c>. Runs off the render thread: reading a coverage sidecar decodes a second
/// full-frame FITS, and the walk measures four edge profiles.
/// </summary>
/// <remarks>
/// <para><b>The exact tier wins wherever it exists.</b> A TianWen drizzle master writes its
/// accumulated per-pixel weight to <c>&lt;master&gt;.rejection.fits</c>, which states the covered
/// area outright; everything else (another program's composite, an older sidecar that cannot say
/// which kind of map it is) gets the estimate. The same split as the mount limits' mechanical
/// tier: a fallback, never a cross-check.</para>
/// <para>Both tiers start from <see cref="Image.LargestCoveredRectangle()"/>, and the walk runs even
/// when that keeps the whole frame. A frame with no canvas ring can still carry the band: a stack
/// someone has already cropped to its zero-free rectangle is exactly that shape, and it is the file
/// most likely to be handed to a viewer. Skipping the walk there to save a second was the first
/// version, and it made the feature silently not apply to the case that needs it most. An ordinary
/// photograph answers "nothing to trim" on its own: vignetting LOWERS a corner's absolute noise
/// along with its signal, so the outermost band is never the noisiest part of the profile.</para>
/// <para><b>What the rule touches.</b> The policy applies to the walk's refused edges only; the coverage
/// plane's rectangle is exact, so it has none. The margin is the caller's explicit request and is applied
/// on either tier, as <c>image autocrop --margin</c> always did.</para>
/// </remarks>
public static class MasterAutoCrop
{
    private const int MinInsetSpan = 16;

    /// <summary>Scans <paramref name="image"/> (the master at <paramref name="filePath"/>, which names the
    /// sidecar) and applies <paramref name="rule"/>.</summary>
    public static MasterAutoCropResult Scan(Image image, string? filePath, MasterAutoCropRule rule, ILogger? logger = null)
    {
        var union = image.LargestCoveredRectangle();
        if (union.Width <= 0 || union.Height <= 0)
        {
            return new MasterAutoCropResult(union, FromCoverage: false, Declined: false);
        }

        if (filePath is { Length: > 0 } path)
        {
            try
            {
                if (IntegrationFitsWriter.TryReadCoverageMap(path, out var coverage))
                {
                    try
                    {
                        var exact = image.CoverageCropRectangle(coverage);
                        if (exact.Width > 0 && exact.Height > 0)
                        {
                            logger?.LogDebug("Auto-crop from the coverage plane: {W}x{H} at ({X},{Y})",
                                exact.Width, exact.Height, exact.X, exact.Y);
                            return WithMargin(image, new MasterAutoCropResult(exact, FromCoverage: true, Declined: false), rule);
                        }
                    }
                    finally
                    {
                        coverage.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                // A sidecar that cannot be read is not a reason to refuse the crop: fall through to the
                // walk, which needs nothing but the pixels already in hand.
                logger?.LogDebug(ex, "Coverage sidecar beside {Path} could not be used", path);
            }
        }

        var trims = CoverageEdgeWalk.Measure(image, union);
        logger?.LogDebug(
            "Auto-crop edge walk: left {L} top {T} right {R} bottom {B} (declined: {Declined})",
            trims.Left.Depth, trims.Top.Depth, trims.Right.Depth, trims.Bottom.Depth, trims.AnyDeclined);

        // The walk's verdicts are applied as a person looking would want them (KeepEveryPixel: a refused
        // edge is left alone), then once more under the rule's policy, so the result can say what the
        // policy took off a declined edge. The stacker and the CLI share CoverageTrimPolicy.Default.
        var viewed = trims.Apply(union, CoverageTrimPolicy.KeepEveryPixel);
        var result = new MasterAutoCropResult(viewed, FromCoverage: false, trims.AnyDeclined, trims);
        if (trims.AnyDeclined && rule.Policy.DeclinedFraction > 0 && viewed.Width > 0 && viewed.Height > 0)
        {
            var held = trims.Apply(viewed, rule.Policy);
            result = held != viewed
                ? result with { Rect = held, DeclinedTrim = new MasterAutoCropApplied(viewed, held) }
                : result with { DeclinedTrimKept = true };
        }

        return WithMargin(image, result, rule);
    }

    /// <summary>Describes each refused edge the rule's policy took off, for a caller that prints it; empty
    /// when it took nothing.</summary>
    public static string DescribeDeclined(MasterAutoCropResult scan, MasterAutoCropRule rule)
    {
        if (scan is not { Trims: { } trims, DeclinedTrim: { } applied })
        {
            return "";
        }

        var rect = applied.Before;
        var policy = rule.Policy;
        return string.Join(", ", new[]
        {
            policy.Describe("left", trims.Left, policy.DepthFor(trims.Left, rect.Width)),
            policy.Describe("top", trims.Top, policy.DepthFor(trims.Top, rect.Height)),
            policy.Describe("right", trims.Right, policy.DepthFor(trims.Right, rect.Width)),
            policy.Describe("bottom", trims.Bottom, policy.DepthFor(trims.Bottom, rect.Height)),
        }.Where(e => e is not null));
    }

    private static MasterAutoCropResult WithMargin(Image image, MasterAutoCropResult scan, MasterAutoCropRule rule)
    {
        var margin = Math.Clamp(rule.Margin, 0.0, 0.25);
        if (margin <= 0)
        {
            return scan;
        }

        var rect = scan.Rect;
        var ix = (int)Math.Round(image.Width * margin);
        var iy = (int)Math.Round(image.Height * margin);
        var inset = new PixelRect(rect.X + ix, rect.Y + iy, rect.Width - 2 * ix, rect.Height - 2 * iy);
        return inset.Width > MinInsetSpan && inset.Height > MinInsetSpan
            ? scan with { Rect = inset, Margin = new MasterAutoCropApplied(rect, inset) }
            : scan with { MarginIgnored = true };
    }
}
