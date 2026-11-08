using System;
using System.Numerics;

namespace TianWen.Lib.Imaging;

/// <summary>
/// How far each colour sat from the reference colour in a frame's own pixels, as the stacker carries it from the measure pass
/// to the warp and the drizzle deposit (#1993): the colour's light lies at (reference + shift), so placing the colour ON the
/// reference means sampling it that far the other way. The same sign as <see cref="ChannelOffset"/>.
/// </summary>
/// <param name="Red">Red's shift from the reference, source px.</param>
/// <param name="Green">Green's shift from the reference, source px (zero when green is the reference).</param>
/// <param name="Blue">Blue's shift from the reference, source px.</param>
/// <remarks>
/// <para><b>The shift is in SOURCE pixels and is taken out BEFORE the registration transform</b>, because dispersion is a
/// property of the optics and the frame's own axes, not of the sky: a field rotation or a flip in the transform turns the
/// registration but not the dispersion. A colour plane is therefore warped by <see cref="Compose"/>, the translation by minus
/// its shift followed by the frame's transform, and a drizzle drop of that colour lands at <see cref="CanvasDelta"/> from where
/// the transform alone would put it.</para>
/// <para><c>default</c> is no shift: every colour is placed by the transform alone, which is what every caller that never
/// measured gets, bit for bit.</para>
/// </remarks>
public readonly record struct ChannelShifts(Vector2 Red, Vector2 Green, Vector2 Blue)
{
    /// <summary>No shift on any colour.</summary>
    public static ChannelShifts None => default;

    /// <summary>Whether no colour moves.</summary>
    public bool IsZero => Red == Vector2.Zero && Green == Vector2.Zero && Blue == Vector2.Zero;

    /// <summary>The longest shift of any colour, px.</summary>
    public float MaxLength => MathF.Max(Red.Length(), MathF.Max(Green.Length(), Blue.Length()));

    /// <summary>
    /// Whole source pixels a strip's source rectangle is widened by so every sample a shifted colour reads is inside it:
    /// the longest shift, rounded up. Zero when nothing moves.
    /// </summary>
    public int HaloPx => (int)MathF.Ceiling(MaxLength);

    /// <summary>One colour's shift, by <see cref="CfaChannel"/> index (0 red, 1 green, 2 blue).</summary>
    public Vector2 this[int channel] => channel switch
    {
        0 => Red,
        1 => Green,
        _ => Blue,
    };

    /// <summary>
    /// The shifts a measurement read, or <see cref="None"/> when it did not read (<see cref="ChannelOffsetsResult.IsMeasured"/>
    /// false): a frame with too few stars aligns with zero offsets, never a guess.
    /// </summary>
    public static ChannelShifts From(ChannelOffsetsResult measured) => measured.IsMeasured
        ? new ChannelShifts(
            new Vector2((float)measured.Red.Dx, (float)measured.Red.Dy),
            new Vector2((float)measured.Green.Dx, (float)measured.Green.Dy),
            new Vector2((float)measured.Blue.Dx, (float)measured.Blue.Dy))
        : None;

    /// <summary>
    /// The transform that places <paramref name="channel"/> of a source frame on the reference colour:
    /// <paramref name="transform"/> with the channel's own source-space translation applied first. Unchanged when the channel
    /// does not move, so an unmeasured frame is warped by exactly the transform it always was.
    /// </summary>
    public Matrix3x2 Compose(int channel, Matrix3x2 transform)
    {
        var shift = this[channel];
        return shift == Vector2.Zero ? transform : Matrix3x2.CreateTranslation(-shift) * transform;
    }

    /// <summary>
    /// Where a drop of <paramref name="channel"/> lands, relative to where <paramref name="transform"/> alone puts it, on the
    /// canvas: the colour's shift taken out in source pixels and carried through the transform's linear part.
    /// </summary>
    public Vector2 CanvasDelta(int channel, Matrix3x2 transform)
        => Vector2.TransformNormal(-this[channel], transform);
}
