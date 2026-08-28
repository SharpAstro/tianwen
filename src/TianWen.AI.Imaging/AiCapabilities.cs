using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AI.Imaging.Onnx;
using TianWen.AI.Imaging.RcAstro;

namespace TianWen.AI.Imaging;

/// <summary>
/// What AI capability this install actually has: which enhancer weights are on disk, and whether a
/// licensed RC-Astro CLI is reachable.
///
/// <para><b>Why it exists.</b> Every one of these failures is silent until the moment someone runs a
/// five-minute enhance. A missing SAS weight file, a Git LFS pointer stub where weights should be, an
/// unlicensed RC product, an <c>rc-astro.exe</c> that is installed but broken -- none of them show up
/// anywhere until the work is already underway, and on a DEPLOYED install nobody has a repo or a dev
/// tool to go looking with. This turns "AI enhance did nothing / crashed" into one report you can ask
/// a user for.</para>
///
/// <para><b>It must be asked for, never run on its own.</b> <c>AddRcAstroAi()</c> deliberately defers
/// the RC-vs-SAS choice and its blocking license probe to the first <c>EnhanceAsync</c>, precisely so
/// that composing a service collection spawns no <c>rc-astro</c> process. A capability probe that ran
/// at startup or during DI would undo that. Each product license check is a process launch, so this
/// is async, and it must not be called from a render thread.</para>
///
/// <para>The model names are NOT listed here. They live as <c>internal const</c> beside the code that
/// loads each one, and <see cref="Requirements"/> references them -- a second list here would be the
/// classic two-copies-drift bug, and the one that drifts is the one nobody runs.</para>
/// </summary>
/// <param name="InstallFolder">Where the running binary lives. On Windows this also identifies the
/// install KIND -- a Store package answers a <c>WindowsApps\...</c> path carrying its package name,
/// version and architecture. Same value the log banner prints.</param>
/// <param name="ModelSearchPaths">Directories searched for weights, in priority order.</param>
/// <param name="Models">One entry per model any enhancer might load.</param>
/// <param name="RcAstro">RC-Astro CLI location and per-product license state.</param>
public sealed record AiCapabilities(
    string InstallFolder,
    ImmutableArray<string> ModelSearchPaths,
    ImmutableArray<AiModelRequirement> Models,
    AiRcAstroStatus RcAstro)
{
    /// <summary>
    /// Every model any enhancer in this assembly might load, paired with the role it serves.
    /// <para>
    /// Deliberately a CANDIDATE list, not a required one: <c>OnnxDenoiser</c> alone accounts for six
    /// entries (mono/colour x Default/Lite/Walking) and a given run needs exactly one of them, so
    /// "3 absent" is normal and healthy. Anything reporting on this must count per ROLE -- a role is
    /// available if any of its candidates resolved -- and never present a raw missing-count as
    /// breakage.
    /// </para>
    /// </summary>
    public static ImmutableArray<(string Capability, string Variant, string FileName)> Requirements =>
    [
        ("Star removal (SAS)", "mono", OnnxStarRemover.MonoModel),
        ("Star removal (SAS)", "colour", OnnxStarRemover.ColorModel),
        ("Stellar sharpen (SAS)", "", OnnxStellarSharpener.Model),
        ("Deconvolve (SAS)", "", OnnxNonStellarDeconvolver.Model),
        ("Gradient correction (GraXpert)", "", OnnxBackgroundExtractor.ModelName),
        ("Denoise (SAS)", "mono", OnnxDenoiser.MonoDefault),
        ("Denoise (SAS)", "colour", OnnxDenoiser.ColorDefault),
        ("Denoise (SAS)", "mono lite", OnnxDenoiser.MonoLite),
        ("Denoise (SAS)", "colour lite", OnnxDenoiser.ColorLite),
        ("Denoise (SAS)", "mono walking", OnnxDenoiser.MonoWalking),
        ("Denoise (SAS)", "colour walking", OnnxDenoiser.ColorWalking),
        ("Denoise (in-house N2N, OSC)", "", N2nDenoiser.ModelFileName),
    ];

    /// <summary>
    /// Probes the host. Pass the resolver the app is actually configured with, so the report
    /// describes the paths that install will really search rather than the defaults.
    /// </summary>
    /// <param name="resolver">Model resolver to interrogate.</param>
    /// <param name="cli">RC-Astro CLI wrapper, or <c>null</c> to skip the RC half entirely (no
    /// process is launched in that case). Take the INTERFACE: <c>AddRcAstroAi</c> registers
    /// <see cref="IRcAstroCli"/>, so asking DI for the concrete type silently resolves null and the
    /// report then says "not probed" on a host that has RC-Astro installed and licensed.</param>
    /// <param name="ct">Cancellation. The license probes are the slow part.</param>
    public static async Task<AiCapabilities> ProbeAsync(
        IModelResolver resolver,
        IRcAstroCli? cli = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var models = ImmutableArray.CreateBuilder<AiModelRequirement>(Requirements.Length);
        foreach (var (capability, variant, fileName) in Requirements)
        {
            ct.ThrowIfCancellationRequested();
            models.Add(new AiModelRequirement(capability, variant, resolver.Probe(fileName)));
        }

        // Off the caller's thread: IsLicensed launches rc-astro once per product and blocks.
        var rc = cli is null
            ? AiRcAstroStatus.NotConfigured
            : await Task.Run(() => ProbeRcAstro(cli), ct).ConfigureAwait(false);

        return new AiCapabilities(BuildInfoInstallFolder(), resolver.SearchPaths, models.ToImmutable(), rc);
    }

    private static AiRcAstroStatus ProbeRcAstro(IRcAstroCli cli)
    {
        if (cli.ExecutablePath is not { Length: > 0 } exe)
        {
            return AiRcAstroStatus.NotFound;
        }

        // The three products TianWen maps to enhancer roles: sxt -> IStarRemover,
        // nxt -> IDenoiseEnhancer, bxt -> INonStellarDeconvolver.
        var products = ImmutableArray.CreateBuilder<AiRcAstroProduct>(3);
        foreach (var key in (string[])["sxt", "nxt", "bxt"])
        {
            products.Add(new AiRcAstroProduct(key, cli.IsLicensed(key)));
        }

        return new AiRcAstroStatus(exe, products.ToImmutable(), Probed: true);
    }

    // Indirection so this assembly does not take a compile-time dependency on TianWen.Lib just for
    // one string; AppContext.BaseDirectory is the same value BuildInfo.InstallFolder reports.
    private static string BuildInfoInstallFolder() => AppContext.BaseDirectory;

    /// <summary>
    /// One line per CAPABILITY -- what this install can do -- with file-level detail only where
    /// something is actually wrong.
    /// <para>
    /// Deliberately not a file listing. Twelve rows of name-and-size is a directory dump: it makes
    /// the reader do the grouping, and it is actively misleading, because three of the six denoise
    /// variants being absent is perfectly healthy (a run uses one). Grouping on capability and
    /// reporting "any resolved" is the only reading that answers the question a user actually has.
    /// </para>
    /// <para>
    /// Indentation is SEMANTIC and one level deep: each capability is a sibling, and only detail
    /// lines nest under their capability. A consumer adds at most one uniform prefix.
    /// </para>
    /// </summary>
    public ImmutableArray<string> Describe()
    {
        var lines = ImmutableArray.CreateBuilder<string>();

        lines.Add(RcAstro switch
        {
            { ExecutablePath: { Length: > 0 } } => RcAstro.Products.Any(p => p.Licensed)
                ? $"RC-Astro: {string.Join(", ", RcAstro.Products.Where(p => p.Licensed).Select(p => p.ProductKey))} licensed"
                : "RC-Astro: installed, nothing licensed -- SAS used instead",
            { Probed: false } => "RC-Astro: not probed",
            _ => "RC-Astro: not installed -- SAS used instead",
        });
        foreach (var p in RcAstro.Products.Where(p => !p.Licensed))
        {
            lines.Add($"  {p.ProductKey}: NOT licensed");
        }

        var anyMissing = false;
        foreach (var group in Models.GroupBy(m => m.Capability))
        {
            var present = group.Where(m => m.Presence.Kind == ModelPresenceKind.Present).ToList();
            var stubs = group.Where(m => m.Presence.Kind == ModelPresenceKind.PointerStub).ToList();
            var total = group.Count();

            // "3 of 6" rather than a bare tick: for a multi-variant capability the count is the
            // difference between "fully installed" and "the one variant you asked for is gone".
            var tally = total > 1 ? $" ({present.Count}/{total} variants)" : "";
            lines.Add(present.Count > 0
                ? $"{group.Key}: available{tally}"
                : $"{group.Key}: UNAVAILABLE{tally}");

            foreach (var stub in stubs)
            {
                lines.Add($"  {stub.Presence.FileName} is an LFS pointer stub -- run 'git lfs pull'");
            }
            if (present.Count == 0)
            {
                anyMissing = true;
                foreach (var m in group)
                {
                    lines.Add($"  missing {m.Presence.FileName}");
                }
            }
        }

        // Only when something is missing, because otherwise it is three lines of noise -- but then
        // it is the actionable half: it says where to put the file.
        //
        // Derived from what was ACTUALLY probed, not from ModelSearchPaths alone. A vendor cache
        // (GraXpert's bge-ai-models/<version>/) is reachable for exactly one model, so it is
        // deliberately absent from the model-agnostic directory list -- but omitting it here made
        // this report claim a search that did not match the one the resolver performs, which is the
        // failure the resolver's own probe list exists to prevent: a list short of a location that
        // really is read gets taken as proof the file is not there.
        if (anyMissing)
        {
            lines.Add("searched (first match wins):");
            var seen = new HashSet<string>(ModelSearchPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var dir in ModelSearchPaths)
            {
                lines.Add($"  {dir}");
            }
            foreach (var extra in Models
                .SelectMany(m => m.Presence.ProbedPaths)
                .Select(p => Path.GetDirectoryName(p))
                .Where(d => !string.IsNullOrEmpty(d))
                .Where(d => seen.Add(d!)))
            {
                lines.Add($"  {extra}  (vendor cache)");
            }
        }

        return lines.ToImmutable();
    }

}

/// <param name="Capability">User-facing capability this model serves. Several models can share one
/// capability (the six denoise variants), which is why the report groups on it: a capability is
/// available when ANY of its models resolved, and a raw missing-count is meaningless.</param>
/// <param name="Variant">Which flavour within the capability (mono / colour / lite / ...), empty
/// when the capability has exactly one model.</param>
/// <param name="Presence">Whether its weights are usable, and where they came from.</param>
public readonly record struct AiModelRequirement(string Capability, string Variant, ModelPresence Presence);

/// <param name="ProductKey">RC-Astro product key (<c>sxt</c> / <c>nxt</c> / <c>bxt</c>).</param>
/// <param name="Licensed">Whether the license probe said yes. A present-but-unlicensed product falls
/// back to the SAS ONNX enhancer rather than failing.</param>
public readonly record struct AiRcAstroProduct(string ProductKey, bool Licensed);

/// <param name="ExecutablePath">Resolved <c>rc-astro</c> path, or <c>null</c> when absent / not asked.</param>
/// <param name="Products">Per-product license state; empty when there is no executable to ask.</param>
/// <param name="Probed">
/// Whether anything actually looked. Load-bearing for a DIAGNOSTIC: "not installed" and "nobody
/// asked" are the same absent path but different facts, and reporting the first when the second is
/// true sends the reader off to reinstall something that was never checked. The two states were
/// briefly indistinguishable here -- both were <c>new(null, [])</c> -- and the report said "not
/// found" for a probe that had deliberately launched nothing.
/// </param>
public sealed record AiRcAstroStatus(
    string? ExecutablePath,
    ImmutableArray<AiRcAstroProduct> Products,
    bool Probed)
{
    /// <summary>No RC-Astro wrapper was supplied, so nothing was launched.</summary>
    public static AiRcAstroStatus NotConfigured { get; } = new(null, [], Probed: false);

    /// <summary>A wrapper was supplied but found no executable to probe.</summary>
    public static AiRcAstroStatus NotFound { get; } = new(null, [], Probed: true);
}
