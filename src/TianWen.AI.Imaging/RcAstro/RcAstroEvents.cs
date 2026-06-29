using System.Text.Json;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// The compute device + final progress reported by a completed RC-Astro
    /// run (parsed from the <c>--json</c> NDJSON event stream).
    /// </summary>
    /// <param name="Device">"gpu" or "cpu" (from the <c>device</c> event), or
    /// null if the run never reported one.</param>
    /// <param name="Provider">Execution-provider detail
    /// ("DirectML"/"CUDA"/"CoreML"/"CPU"); informational.</param>
    /// <param name="LastProgress">The final <c>progress</c> tick seen.</param>
    public sealed record RcAstroRunResult(string? Device, string? Provider, RcAstroProgress LastProgress);

    /// <summary>A single progress tick from RC-Astro's NDJSON event stream.</summary>
    /// <param name="PercentDone">Overall completion 0-100, climbs monotonically.</param>
    /// <param name="MegapixelsPerSecond">Smoothed throughput; 0 very early in a job.</param>
    /// <param name="EtaSeconds">Estimated seconds remaining.</param>
    public readonly record struct RcAstroProgress(double PercentDone, double MegapixelsPerSecond, double EtaSeconds);

    /// <summary>
    /// One parsed line of RC-Astro's NDJSON event stream (schemaVersion 3): a
    /// flattened union of the fields across the
    /// status/device/progress/warning/error/info event types. Consumers switch
    /// on <see cref="Kind"/> and read only the fields relevant to that kind.
    /// </summary>
    /// <remarks>
    /// Parsed with <see cref="JsonDocument"/> (no reflection / no source-gen
    /// context) so it stays AOT- and trim-clean. Unknown event types and
    /// unknown keys are tolerated per the protocol's forward-compatibility
    /// rules: an unrecognised <see cref="Kind"/> is simply ignored by callers,
    /// and extra keys are never read.
    /// </remarks>
    internal sealed record RcAstroEvent(
        string Kind,
        string? Phase = null,
        string? Message = null,
        string? Output = null,
        string? Device = null,
        string? DeviceName = null,
        string? Provider = null,
        double? Done = null,
        double? MpPerSec = null,
        double? Eta = null,
        string? Topic = null)
    {
        /// <summary>
        /// Parses one NDJSON line. Returns null for a blank line, a non-object
        /// payload, a payload with no <c>event</c> discriminator, or a line
        /// that is not JSON at all (e.g. a pre-product CLI-usage error that the
        /// protocol prints as plain stderr/stdout text).
        /// </summary>
        public static RcAstroEvent? TryParse(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("event", out var kindProp)
                    || kindProp.GetString() is not { } kind)
                {
                    return null;
                }

                return new RcAstroEvent(
                    Kind: kind,
                    Phase: GetString(root, "phase"),
                    Message: GetString(root, "message"),
                    Output: GetString(root, "output"),
                    Device: GetString(root, "device"),
                    DeviceName: GetString(root, "name"),
                    Provider: GetString(root, "provider"),
                    Done: GetDouble(root, "done"),
                    MpPerSec: GetDouble(root, "mpPerSec"),
                    Eta: GetDouble(root, "eta"),
                    Topic: GetString(root, "topic"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? GetString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? GetDouble(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }
}
