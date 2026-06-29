using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
        private readonly string _engine;
        private readonly ConcurrentDictionary<string, bool> _licenseCache = new();

        /// <param name="logger">Optional diagnostics logger.</param>
        /// <param name="engine">Value for <c>--engine</c>: "auto" (default; resolves
        /// to GPU/DirectML when available), "dml", or "cpu".</param>
        public RcAstroCli(ILogger<RcAstroCli>? logger = null, string engine = "auto")
        {
            _logger = logger;
            _engine = engine;
            ExecutablePath = LocateExecutable();
            if (ExecutablePath is not null)
            {
                _logger?.LogDebug("RC-Astro CLI located at {Path} (engine={Engine})", ExecutablePath, _engine);
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

        /// <inheritdoc/>
        public bool IsLicensed(string productKey)
            => ExecutablePath is not null && _licenseCache.GetOrAdd(productKey, ProbeLicense);

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
            psi.ArgumentList.Add("--engine");
            psi.ArgumentList.Add(_engine);
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
            var stderr = new StringBuilder();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                {
                    lock (stderr) { stderr.AppendLine(line); }
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
                            _logger?.LogDebug("RC-Astro {Product} on {Device} ({Provider} {Name})",
                                productKey, ev.Device, ev.Provider, ev.DeviceName);
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
                    ?? (stderr.Length > 0 ? stderr.ToString().Trim() : "no diagnostic output");
                throw new RcAstroCliException($"RC-Astro '{productKey}' failed (exit {proc.ExitCode}): {detail}");
            }

            return new RcAstroRunResult(device, provider, lastProgress);
        }

        private bool ProbeLicense(string productKey)
        {
            try
            {
                var psi = new ProcessStartInfo(ExecutablePath!)
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
                    return false;
                }

                // Output is a small banner + one status line; ReadToEnd returns
                // when the stream closes (on exit), so this cannot deadlock.
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(LicenseProbeTimeoutMs);

                // "<Product>: Permanently licensed." / "Trial license expires..."
                // both count as licensed; "not licensed" / "expired" do not.
                var licensed = proc.ExitCode == 0
                    && stdout.Contains("licensed", StringComparison.OrdinalIgnoreCase)
                    && !stdout.Contains("not licensed", StringComparison.OrdinalIgnoreCase)
                    && !stdout.Contains("expired", StringComparison.OrdinalIgnoreCase);

                _logger?.LogDebug("RC-Astro license probe {Product}: {Status} (exit {Exit})",
                    productKey, licensed ? "licensed" : "unlicensed", proc.ExitCode);
                return licensed;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "RC-Astro license probe failed for {Product}.", productKey);
                return false;
            }
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
