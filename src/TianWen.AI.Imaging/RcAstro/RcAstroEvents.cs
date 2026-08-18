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
    /// One parsed line of RC-Astro's NDJSON event stream (schemaVersion 3 and 4): a
    /// flattened union of the fields across the
    /// status/device/progress/warning/error/info event types. Consumers switch
    /// on <see cref="Kind"/> and read only the fields relevant to that kind.
    /// </summary>
    /// <remarks>
    /// <para>Parsed with <see cref="JsonDocument"/> (no reflection / no source-gen
    /// context) so it stays AOT- and trim-clean. Unknown event types and
    /// unknown keys are tolerated per the protocol's forward-compatibility
    /// rules: an unrecognised <see cref="Kind"/> is simply ignored by callers,
    /// and extra keys are never read.</para>
    ///
    /// <para><b>The compute device is reported as an <c>info</c> event carrying
    /// <c>topic: "device"</c></b>, not as an event kind of its own:
    /// <c>{"event":"info","topic":"device","device":"cpu","id":"cpu","name":"",
    /// "provider":"CPU","runtime":"onnxruntime 1.23.2"}</c>. Reading it as a
    /// <c>device</c> KIND -- which schema 3 was believed to use -- silently
    /// produced null, so every run logged "completed on ?" and a silent GPU
    /// fallback was invisible for as long as it had been happening. Both
    /// spellings are accepted here so an older CLI keeps working.</para>
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

                // An info event that carries topic "device" IS the device report; normalising it
                // here keeps the one switch in RcAstroCli honest about what it is looking at.
                var topic = GetString(root, "topic");
                if (kind == "info" && topic == "device")
                {
                    kind = "device";
                }

                return new RcAstroEvent(
                    Kind: kind,
                    Phase: GetString(root, "phase"),
                    Message: GetString(root, "message"),
                    Output: GetString(root, "output"),
                    // "device" is the schema 4 spelling ("cpu"/"gpu"); "id" is its identifier
                    // ("cpu"/"gpu"/"gpu1"), which is what an older CLI reported instead.
                    Device: GetString(root, "device") ?? GetString(root, "id"),
                    DeviceName: GetString(root, "name"),
                    Provider: GetString(root, "provider"),
                    Done: GetDouble(root, "done"),
                    MpPerSec: GetDouble(root, "mpPerSec"),
                    Eta: GetDouble(root, "eta"),
                    Topic: topic);
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
