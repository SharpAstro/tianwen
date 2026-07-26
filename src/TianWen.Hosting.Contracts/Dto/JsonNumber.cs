using System.Text.Json.Serialization;

namespace TianWen.Hosting.Dto
{
    /// <summary>
    /// Policy gate for floating point values crossing the wire: passes them through when the JSON
    /// contract can represent non-finite numbers, and substitutes a fallback when it cannot.
    /// <para>
    /// <b>Why a policy and not a hardcoded coercion.</b> Whether NaN and Infinity are representable is a
    /// property of the <i>contract</i> (<see cref="HostingJsonContext"/>'s
    /// <see cref="JsonNumberHandling"/>), not of any individual DTO. Coercing unconditionally at ~30 call
    /// sites would hardcode that decision in the wrong place and make it lossy for no reason the moment
    /// the contract changes: NaN means <i>"not known"</i>, and 0 is a real reading, so a client cannot
    /// tell "the mount is at RA 0" from "the mount has not been polled". So the substitution happens only
    /// while <see cref="WireAllowsNonFinite"/> is false. Flip the contract to
    /// <see cref="JsonNumberHandling.AllowNamedFloatingPointLiterals"/> and every call site starts
    /// preserving NaN with no edit.
    /// </para>
    /// <para>
    /// <b>Why it must be applied at all.</b> With the current (strict) contract,
    /// <c>Utf8JsonWriter.WriteNumberValue</c> throws on a non-finite value -- and because serialization
    /// runs while the response is already streaming, the caller gets a <b>bodiless HTTP 500 for the whole
    /// endpoint</b>. One unknown focuser temperature takes down the entire session-state payload. NaN is
    /// the ordinary "not known" value throughout the domain (<c>MountState</c> before the first poll,
    /// <c>CameraExposureState.FocuserTemperature</c> with no focuser fitted, a weather property the
    /// station does not measure), so this is the healthy path, not an edge case.
    /// </para>
    /// <para>
    /// <b>Why 0 is the default fallback:</b> it is what N.I.N.A. itself reports for an unavailable
    /// reading, so the ninaAPI shim stays compatible, and native-v1 clients already saw 0 for a pre-poll
    /// mount. Enabling named literals instead would emit the non-standard <c>"NaN"</c> token, which real
    /// nina clients do not parse -- which is exactly why the policy is currently off.
    /// </para>
    /// </summary>
    public static class JsonNumber
    {
        /// <summary>
        /// Whether the wire contract can carry NaN / Infinity, <b>derived from the serializer options
        /// themselves</b> rather than declared alongside them -- a parallel declaration is precisely
        /// what would drift from the contract it claims to describe.
        /// <para>
        /// This reads <see cref="HostingJsonContext"/> because it is the context in this assembly;
        /// <c>NinaApiJsonContext</c> (in TianWen.Hosting) must agree, which is asserted by
        /// <c>HostingWireNumberTests</c> rather than assumed.
        /// </para>
        /// </summary>
        public static bool WireAllowsNonFinite { get; } =
            HostingJsonContext.Default.Options.NumberHandling
                .HasFlag(JsonNumberHandling.AllowNamedFloatingPointLiterals);

        /// <summary>
        /// Returns <paramref name="value"/> as the wire can carry it: unchanged when
        /// <see cref="WireAllowsNonFinite"/>, otherwise <paramref name="fallback"/> for NaN and infinities.
        /// </summary>
        public static double ForWire(double value, double fallback = 0.0) =>
            WireAllowsNonFinite || double.IsFinite(value) ? value : fallback;

        /// <inheritdoc cref="ForWire(double, double)"/>
        public static float ForWire(float value, float fallback = 0f) =>
            WireAllowsNonFinite || float.IsFinite(value) ? value : fallback;

        /// <summary>
        /// The wire encoding of "not known". Use it for a pre-built DTO -- a <c>Disconnected</c>
        /// sentinel, say -- whose literal value <i>is</i> what goes out, so that it follows the same
        /// policy as a value routed through <see cref="ForWire(double, double)"/> instead of hardcoding
        /// the coercion a second time where it cannot be re-decided.
        /// </summary>
        public static double Unknown { get; } = ForWire(double.NaN);
    }
}
