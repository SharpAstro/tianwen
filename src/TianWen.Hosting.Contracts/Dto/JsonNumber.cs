namespace TianWen.Hosting.Dto
{
    /// <summary>
    /// Coerces non-finite floating point values to a JSON-representable number.
    /// <para>
    /// <b>Why this must be applied at every wire boundary.</b> JSON has no NaN or Infinity, and none of
    /// our contexts enable <c>JsonNumberHandling.AllowNamedFloatingPointLiterals</c> (which would emit
    /// the non-standard <c>"NaN"</c> token that real clients do not parse). So
    /// <c>Utf8JsonWriter.WriteNumberValue</c> throws on one, and because serialization happens while the
    /// response is already streaming, the failure surfaces as a <b>bodiless HTTP 500 for the whole
    /// endpoint</b> -- one unknown focuser temperature takes down the entire session-state payload.
    /// </para>
    /// <para>
    /// NaN is the <i>normal</i> "not known yet" value throughout the domain -- <c>MountState</c> before
    /// the first poll, <c>CameraExposureState.FocuserTemperature</c> when no focuser is fitted, a weather
    /// property the driver does not report -- so this is not an edge case to assert away. Zero is the
    /// right fallback for the wire: it is what N.I.N.A. itself reports for an unavailable reading, so the
    /// ninaAPI shim stays compatible, and native-v1 clients already saw 0 for a pre-poll mount.
    /// </para>
    /// </summary>
    public static class JsonNumber
    {
        /// <summary>Returns <paramref name="value"/>, or <paramref name="fallback"/> when it is NaN or infinite.</summary>
        public static double Finite(double value, double fallback = 0.0) =>
            double.IsFinite(value) ? value : fallback;

        /// <inheritdoc cref="Finite(double, double)"/>
        public static float Finite(float value, float fallback = 0f) =>
            float.IsFinite(value) ? value : fallback;
    }
}
