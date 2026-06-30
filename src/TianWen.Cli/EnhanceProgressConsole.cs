using System;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Cli;

/// <summary>
/// Builds a console-printing <see cref="IProgress{T}"/> of <see cref="EnhanceProgress"/>
/// for the AI sharpen pipeline, mirroring the <c>StackingProgress</c> console hook in
/// <see cref="StackSubCommand"/>: one line per step transition plus rate-limited
/// sub-step percent ticks (the RC-Astro NDJSON stream) at ~10% granularity, so a
/// multi-minute deblur / denoise step isn't a silent terminal.
/// </summary>
/// <remarks>
/// The rate-limit state is captured per <see cref="Create"/> call, so each command
/// invocation gets an independent printer. Both <c>image sharpen</c> and
/// <c>stack --enhance</c> share this single implementation via different prefixes.
/// </remarks>
internal static class EnhanceProgressConsole
{
    /// <param name="consoleHost">Sink for the scrollable progress lines.</param>
    /// <param name="prefix">Leading tag for each line (e.g. <c>[sharpen]</c> or
    /// <c>[stack] enhance:</c>).</param>
    public static IProgress<EnhanceProgress> Create(IConsoleHost consoleHost, string prefix)
    {
        var lastStep = -1;
        var lastPct = -1;
        return new Progress<EnhanceProgress>(p =>
        {
            // Step transition: always announce the new step and reset the sub-step
            // rate-limit so the next step's ticks print from scratch.
            if (p.StepIndex != lastStep)
            {
                lastStep = p.StepIndex;
                lastPct = -1;
                consoleHost.WriteScrollable($"{prefix} step {p.StepIndex + 1}/{p.StepCount}: {p.StepName}");
            }

            // Sub-step percent (RC-Astro NDJSON). CPU-only steps emit no sub-ticks,
            // so they show only the step line above. Print on >= 10% jumps (and the
            // terminal 100%) to avoid flooding on fast frames.
            var pct = (int)(100f * Math.Clamp(p.StepPercent, 0f, 1f));
            if (pct > 0 && (pct >= lastPct + 10 || pct == 100))
            {
                lastPct = pct;
                var eta = p.EtaSeconds > 0 ? $" (eta {p.EtaSeconds:F0}s)" : "";
                consoleHost.WriteScrollable($"{prefix} {p.StepName} {pct}%{eta}");
            }
        });
    }
}
