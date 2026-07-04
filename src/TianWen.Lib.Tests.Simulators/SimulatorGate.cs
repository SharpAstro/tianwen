using System;

namespace TianWen.Lib.Tests.Simulators;

/// <summary>
/// Central opt-in gates for the simulator-backed integration tests. Each suite talks to a real
/// ASCOM / Alpaca simulator provisioned out-of-process (by CI or a developer), so it is skipped
/// unless the corresponding environment variable is set. A bare <c>dotnet test</c> on a machine
/// with no simulator therefore stays green (every gated test reports Skipped, not Failed).
/// </summary>
internal static class SimulatorGate
{
    /// <summary>Base URL of a running ASCOM Alpaca ("OmniSim") server, e.g. <c>http://localhost:11111</c>.</summary>
    public const string AlpacaBaseUrlVar = "TIANWEN_ALPACA_SIM";

    /// <summary>Any non-empty value opts in the native ASCOM COM tests; the ASCOM Platform + simulators
    /// must be installed (Windows only).</summary>
    public const string AscomCiVar = "TIANWEN_ASCOM_CI";

    /// <summary>Serial port of a physically-connected Gemini FlatPanel Lite, e.g. <c>serial:COM3</c> or bare
    /// <c>COM3</c> / <c>/dev/ttyUSB0</c>. Named for the FlatPanel Lite specifically (Gemini Astro ships many
    /// other devices). Unlike the ASCOM/Alpaca sims this needs REAL hardware on a real port -- there is no
    /// Gemini simulator -- so it is opt-in and skips when unset.</summary>
    public const string GeminiFlatPanelPortVar = "TIANWEN_GEMINI_FPLITE_PORT";

    /// <summary>The configured Alpaca simulator base URL (trailing slash trimmed), or <see langword="null"/> when unset.</summary>
    public static string? AlpacaBaseUrl =>
        Environment.GetEnvironmentVariable(AlpacaBaseUrlVar) is { Length: > 0 } url ? url.TrimEnd('/') : null;

    /// <summary>Whether the native ASCOM COM tests are opted in.</summary>
    public static bool AscomCiEnabled =>
        Environment.GetEnvironmentVariable(AscomCiVar) is { Length: > 0 };

    /// <summary>The Gemini FlatPanel Lite serial port, normalised to the <c>serial:</c> URI form, or
    /// <see langword="null"/> when unset. A bare port name (<c>COM3</c>) gains the <c>serial:</c> prefix.</summary>
    public static string? GeminiFlatPanelPort =>
        Environment.GetEnvironmentVariable(GeminiFlatPanelPortVar) is { Length: > 0 } port
            ? (port.Contains(':') ? port : $"serial:{port}")
            : null;
}
