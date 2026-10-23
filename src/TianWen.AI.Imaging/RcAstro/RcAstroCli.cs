using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// Default <see cref="IRcAstroCli"/>: locates <c>rc-astro</c> (env override
    /// -&gt; platform install dir -&gt; PATH), probes per-product licenses, and
    /// runs products with NDJSON parsing. Modeled on
    /// <c>ExternalProcessPlateSolverBase</c>'s redirected-process pattern.
    /// </summary>
    public sealed class RcAstroCli : IRcAstroCli
    {
        /// <summary>Env var pointing directly at the rc-astro executable (highest priority).</summary>
        public const string ExecutableOverrideEnvVar = "RC_ASTRO_CLI";

        // The license probe prints a banner + one status line and exits fast;
        // bound it anyway so a wedged binary can't stall composition-root setup.
        private const int LicenseProbeTimeoutMs = 15_000;

        private readonly ILogger<RcAstroCli>? _logger;
        // Only a GRANTED licence is remembered. A refusal is asked again on the next call: the probe has
        // refused a licensed product (exit 77 on 2026-10-23, licensed again ten minutes later with nothing
        // changed but `rc-astro license` run by hand), and a cached refusal kept every enhance in that
        // process on the whole-frame program until it restarted. The callers ask once per enhance, a few
        // times, so a refused product costs a few short probes per enhance and nothing per frame.
        private readonly ConcurrentDictionary<string, byte> _licensed = new();
        private readonly ConcurrentDictionary<string, string> _refusals = new();
        private readonly Func<string, string, LicenseProbe> _probe;

        /// <param name="logger">Optional diagnostics logger.</param>
        public RcAstroCli(ILogger<RcAstroCli>? logger = null)
            : this(LocateExecutable(), RunLicenseProbe, logger)
        {
        }

        /// <summary>Test seam: a located executable and the probe that asks it.</summary>
        internal RcAstroCli(string? executablePath, Func<string, string, LicenseProbe> probe, ILogger<RcAstroCli>? logger = null)
        {
            _logger = logger;
            _probe = probe;
            ExecutablePath = executablePath;
            if (ExecutablePath is not null)
            {
                _logger?.LogDebug("RC-Astro CLI located at {Path}", ExecutablePath);
            }
            else
            {
                _logger?.LogDebug("RC-Astro CLI not found; enhancers will fall back to the ONNX backend.");
            }
        }

        /// <inheritdoc/>
        public string? ExecutablePath { get; }

        /// <inheritdoc/>
        public bool IsAvailable => ExecutablePath is not null;

        /// <summary>
        /// Cheap filesystem check (no subprocess) for whether the rc-astro CLI is
        /// installed. Gates <c>IImageDeblurrer</c> registration -- and thereby the
        /// BlurX-first canonical -- without a startup license probe (the bxt
        /// license check stays deferred to first use).
        /// </summary>
        public static bool IsInstalled => LocateExecutable() is not null;

        /// <inheritdoc/>
        public bool IsLicensed(string productKey)
        {
            if (ExecutablePath is not { } exePath)
            {
                return false;
            }
            if (_licensed.ContainsKey(productKey))
            {
                return true;
            }

            var probe = _probe(exePath, productKey);
            _logger?.LogDebug("RC-Astro license probe {Product}: {Status} (exit {Exit})",
                productKey, probe.Licensed ? "licensed" : "unlicensed", probe.ExitCode);
            if (probe.Licensed)
            {
                _licensed.TryAdd(productKey, 0);
                _refusals.TryRemove(productKey, out _);
                return true;
            }

            var refusal = probe.ExitCode is { } exit
                ? $"its licence check exited {exit}{(probe.StatusLine is { Length: > 0 } line ? $": {line}" : "")}"
                : $"its licence check could not run{(probe.StatusLine is { Length: > 0 } why ? $": {why}" : "")}";
            _refusals[productKey] = refusal;
            _logger?.LogInformation("RC-Astro {Product} refused: {Refusal}", productKey, refusal);
            return false;
        }

        /// <inheritdoc/>
        public string? LicenseRefusal(string productKey)
            => ExecutablePath is null ? "RC-Astro is not installed"
                : _refusals.TryGetValue(productKey, out var refusal) ? refusal
                : null;

        /// <inheritdoc/>
        public async Task<RcAstroRunResult> RunAsync(
            string productKey,
            string inputPath,
            string outputPath,
            IReadOnlyList<string> extraArgs,
            IProgress<RcAstroProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (ExecutablePath is not { } exe)
            {
                throw new RcAstroCliException(
                    $"RC-Astro CLI executable not found. Install it or set the {ExecutableOverrideEnvVar} environment variable.");
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(productKey);
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outputPath);
            psi.ArgumentList.Add("--depth");
            psi.ArgumentList.Add("32F");
            // Compute device is left to the CLI default (auto -> GPU/DirectML when available). We used to
            // pass "--engine auto", but RC-Astro renamed that flag to "--device" (v0.9.x), which made the
            // old flag a hard error (exit 109). Omitting it entirely keeps us decoupled from the flag name
            // across CLI versions -- "auto" is the default anyway.
            //
            // WHICH device that is stays RC-Astro's own saved setting, and on a machine with two GPUs
            // its "auto" can pick the weaker one: on the x64 desktop it resolved to the Intel UHD 630
            // beside a GTX 1070, which is 5m53s against 44.7s on one 30 MP master for byte-identical
            // output. Choosing here would mean ranking a machine's adapters ourselves, so instead the
            // setting is documented as a once-per-machine check (`rc-astro --device`,
            // `--device-default gpuN`): docs/plans/rc-astro-enhancers.md, "WHICH GPU".
            psi.ArgumentList.Add("--overwrite");
            psi.ArgumentList.Add("--json");
            foreach (var arg in extraArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi)
                ?? throw new RcAstroCliException($"Failed to start RC-Astro CLI process for product '{productKey}'.");

            // stderr stays empty on success in --json mode; capture it anyway so
            // a hard failure (which can still print there) has a diagnostic.
            // The handler runs on a pool thread, so the lines go into a lock-free queue and are joined
            // once the process has exited.
            var stderr = new ConcurrentQueue<string>();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                {
                    stderr.Enqueue(line);
                }
            };
            proc.BeginErrorReadLine();

            string? device = null;
            string? provider = null;
            string? errorMessage = null;
            var lastProgress = default(RcAstroProgress);

            try
            {
                while (await proc.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (RcAstroEvent.TryParse(line) is not { } ev)
                    {
                        continue;
                    }

                    switch (ev.Kind)
                    {
                        case "device":
                            device = ev.Device;
                            provider = ev.Provider;
                            // We ask for no device, so the CLI picks: it reports "gpu" when it got
                            // one and "cpu" when it did not. The fallback is otherwise SILENT --
                            // RC-Astro only warns when a device was explicitly REQUESTED and denied,
                            // so with our default this event is the only sign, and a GPU that stops
                            // working (a cached "adapter failed every DirectML strategy" verdict, a
                            // driver change) would just look like a slow machine. Say it out loud.
                            if (string.Equals(ev.Device, "cpu", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger?.LogWarning(
                                    "RC-Astro {Product} is running on the CPU ({Provider}); no GPU was available to it. "
                                    + "Run 'rc-astro --device' to see what it can find, and "
                                    + "'rc-astro --device-default auto' to clear a cached GPU verdict.",
                                    productKey, ev.Provider);
                            }
                            else
                            {
                                _logger?.LogInformation("RC-Astro {Product} on {Device} ({Provider} {Name})",
                                    productKey, ev.Device, ev.Provider, ev.DeviceName);
                            }
                            break;

                        case "progress":
                            lastProgress = new RcAstroProgress(ev.Done ?? 0.0, ev.MpPerSec ?? 0.0, ev.Eta ?? 0.0);
                            progress?.Report(lastProgress);
                            break;

                        case "warning":
                            _logger?.LogWarning("RC-Astro {Product}: {Message}", productKey, ev.Message);
                            break;

                        case "error":
                            // First error wins; keep streaming so the process can
                            // exit cleanly, then surface it below.
                            errorMessage ??= ev.Message;
                            _logger?.LogError("RC-Astro {Product}: {Message}", productKey, ev.Message);
                            break;

                        case "info":
                            _logger?.LogInformation("RC-Astro {Product} [{Topic}]: {Message}",
                                productKey, ev.Topic, ev.Message);
                            break;

                        // "status" is lifecycle only; nothing to surface.
                    }
                }

                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Never leave an orphaned rc-astro process on cancellation or
                // a mid-stream throw.
                if (!proc.HasExited)
                {
                    try { proc.Kill(entireProcessTree: true); }
                    catch (Exception ex) { _logger?.LogDebug(ex, "RC-Astro {Product}: failed to kill process during cleanup.", productKey); }
                }
            }

            if (proc.ExitCode != 0 || errorMessage is not null)
            {
                var detail = errorMessage
                    ?? (string.Join(Environment.NewLine, stderr).Trim() is { Length: > 0 } text ? text : "no diagnostic output");
                throw new RcAstroCliException($"RC-Astro '{productKey}' failed (exit {proc.ExitCode}): {detail}");
            }

            return new RcAstroRunResult(device, provider, lastProgress);
        }

        /// <summary>What one <c>rc-astro &lt;product&gt; --license</c> said: whether it grants the product, its
        /// exit code (null when it never ran or never exited), and its last line of output, the words to show
        /// when it refuses.</summary>
        internal readonly record struct LicenseProbe(bool Licensed, int? ExitCode, string? StatusLine);

        private static LicenseProbe RunLicenseProbe(string exePath, string productKey)
        {
            try
            {
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(productKey);
                psi.ArgumentList.Add("--license");

                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    return new LicenseProbe(false, null, "the process did not start");
                }

                // stderr drains on its own thread, so neither pipe can fill while the other is read; it is kept
                // because a refusal may say why only there.
                var stderr = new ConcurrentQueue<string>();
                proc.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) stderr.Enqueue(line); };
                proc.BeginErrorReadLine();

                // Output is a small banner + one status line; ReadToEnd returns
                // when the stream closes (on exit), so this cannot deadlock.
                var stdout = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(LicenseProbeTimeoutMs))
                {
                    return new LicenseProbe(false, null, $"no answer in {LicenseProbeTimeoutMs / 1000} s");
                }
                // The parameterless wait flushes the redirected stderr handler once the timed wait saw the exit.
                proc.WaitForExit();

                // "<Product>: Permanently licensed." / "Trial license expires..."
                // both count as licensed; "not licensed" / "expired" do not.
                var licensed = proc.ExitCode == 0
                    && stdout.Contains("licensed", StringComparison.OrdinalIgnoreCase)
                    && !stdout.Contains("not licensed", StringComparison.OrdinalIgnoreCase)
                    && !stdout.Contains("expired", StringComparison.OrdinalIgnoreCase);

                return new LicenseProbe(licensed, proc.ExitCode, LastLine(stderr.IsEmpty ? stdout : string.Join('\n', stderr)));
            }
            catch (Exception ex)
            {
                return new LicenseProbe(false, null, ex.Message);
            }
        }

        /// <summary>The last non-blank line of a probe's output, trimmed: the status line, after the banner.</summary>
        internal static string? LastLine(string text)
        {
            var span = text.AsSpan().TrimEnd();
            var newline = span.LastIndexOfAny('\r', '\n');
            var line = (newline < 0 ? span : span[(newline + 1)..]).Trim();
            return line.IsEmpty ? null : line.ToString();
        }

        private static string? LocateExecutable()
        {
            var overridePath = Environment.GetEnvironmentVariable(ExecutableOverrideEnvVar);
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                return overridePath;
            }

            foreach (var candidate in DefaultInstallCandidates())
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return FindOnPath(OperatingSystem.IsWindows() ? "rc-astro.exe" : "rc-astro");
        }

        private static IEnumerable<string> DefaultInstallCandidates()
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var programFiles in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                })
                {
                    if (!string.IsNullOrEmpty(programFiles))
                    {
                        yield return Path.Combine(programFiles, "RC-Astro", "CLI", "rc-astro.exe");
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                yield return "/Applications/RC-Astro/CLI/rc-astro";
            }
            else
            {
                yield return "/opt/rc-astro/rc-astro";
                yield return "/opt/rc-astro/CLI/rc-astro";
            }
        }

        private static string? FindOnPath(string exeName)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
