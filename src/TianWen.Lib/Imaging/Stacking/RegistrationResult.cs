using System;
using System.Numerics;
using System.Text.Json.Serialization;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Result of registering one light frame against a reference: the affine
/// transform from light-pixel space to reference-pixel space, plus a few
/// diagnostic fields the integrator uses for rejection weighting and the user
/// uses for QA. Persisted as a <c>&lt;light&gt;.match.json</c> sidecar so the
/// expensive star-match path runs once per light per session, not once per
/// integration run.
/// </summary>
/// <param name="LightPath">Path to the light frame this transform is for.
/// Round-tripped through JSON; the integrator uses it to verify the sidecar
/// applies to the right file.</param>
/// <param name="M11">First column of the affine 2x2 — <see cref="Matrix3x2"/> M11.</param>
/// <param name="M12">First column of the affine 2x2 — <see cref="Matrix3x2"/> M12.</param>
/// <param name="M21">Second column of the affine 2x2 — <see cref="Matrix3x2"/> M21.</param>
/// <param name="M22">Second column of the affine 2x2 — <see cref="Matrix3x2"/> M22.</param>
/// <param name="OffsetX">Translation x (<see cref="Matrix3x2"/> M31).</param>
/// <param name="OffsetY">Translation y (<see cref="Matrix3x2"/> M32).</param>
/// <param name="StarsMatched">Number of star quads matched against the
/// reference. -1 = unknown (the wrapped FindOffsetAndRotationAsync API
/// doesn't return this today; left as a forward-compat field).</param>
/// <param name="Registered">True if quad match succeeded; false means the
/// transform is identity (light is left unrotated/untranslated) but the
/// integrator should treat the frame as unalignable and likely reject.</param>
/// <param name="ComputedUtc">Wall-clock time the registration was computed.
/// Used for sidecar staleness checks (if the light file mtime is newer than
/// this, the sidecar is recomputed on next run).</param>
public sealed record RegistrationResult(
    string LightPath,
    float M11,
    float M12,
    float M21,
    float M22,
    float OffsetX,
    float OffsetY,
    int StarsMatched,
    bool Registered,
    DateTimeOffset ComputedUtc)
{
    /// <summary>The affine transform as a <see cref="Matrix3x2"/> — source
    /// (light) pixels mapped into the reference frame's coordinate system.</summary>
    [JsonIgnore]
    public Matrix3x2 ToReference => new(M11, M12, M21, M22, OffsetX, OffsetY);

    /// <summary>Identity-transform shorthand: light is the reference, or
    /// registration failed and the integrator should skip the frame.</summary>
    public static RegistrationResult Identity(string lightPath, bool registered) =>
        new(lightPath, 1f, 0f, 0f, 1f, 0f, 0f, registered ? -1 : 0, registered, DateTimeOffset.UtcNow);

    /// <summary>Builds a result from the <see cref="Matrix3x2"/> returned by
    /// <see cref="Image.FindOffsetAndRotationAsync"/>.</summary>
    public static RegistrationResult FromTransform(string lightPath, Matrix3x2 transform, int starsMatched = -1) =>
        new(lightPath,
            M11: transform.M11, M12: transform.M12,
            M21: transform.M21, M22: transform.M22,
            OffsetX: transform.M31, OffsetY: transform.M32,
            StarsMatched: starsMatched,
            Registered: true,
            ComputedUtc: DateTimeOffset.UtcNow);
}

[JsonSerializable(typeof(RegistrationResult))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class RegistrationJsonContext : JsonSerializerContext;
