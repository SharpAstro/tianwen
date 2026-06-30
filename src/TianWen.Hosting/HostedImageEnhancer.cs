using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Hosting;

/// <summary>
/// Server-side single-flight AI image enhancer. Runs <see cref="SharpenPipeline.ProcessAsync(SharpenRequest, EnhanceOptions, System.IProgress{EnhanceProgress}, CancellationToken)"/>
/// on a background task (RC-Astro deblur/denoise is tens of seconds), so <c>POST /api/v1/image/enhance</c>
/// returns immediately and the client follows progress via the <c>ENHANCE-PROGRESS</c> /
/// <c>ENHANCE-COMPLETED</c> WebSocket events (<see cref="WebSocket.EventBroadcaster"/>) or by polling
/// <c>GET /api/v1/image/enhance/status</c>. The status snapshot is an immutable
/// <see cref="EnhanceStatusDto"/> swapped atomically per tick (lock-free read); the in-flight gate is an
/// <see cref="Interlocked"/> CAS so a second POST while one is running is rejected (409) rather than racing.
/// </summary>
/// <remarks>
/// The <see cref="SharpenPipeline"/> is <b>optional</b>: it is registered only by <c>AddRcAstroAi()</c> /
/// <c>AddTianWenAi()</c>, which a host (e.g. the functional-test host, or a headless server with no AI
/// models) need not wire. When absent <see cref="IsAvailable"/> is <c>false</c> and the endpoint returns
/// 503 -- mirroring the viewer's presence-gated Enhance button (renderer <c>EnhanceAvailable</c>). Resolving
/// the dependency via <c>GetService</c> (not <c>GetRequiredService</c>) is what keeps a no-AI host startable.
/// </remarks>
internal sealed class HostedImageEnhancer(
    SharpenPipeline? pipeline,
    ILogger<HostedImageEnhancer> logger)
{
    // 0 = idle, 1 = a run is in flight. CAS on start; reset in the run's finally.
    private int _busy;

    // Reference-typed, so the field write is atomic; readers see a fully-built snapshot.
    private volatile EnhanceStatusDto _status = new EnhanceStatusDto { IsEnhancing = false };

    /// <summary>Latest immutable status snapshot (safe to read from any thread).</summary>
    public EnhanceStatusDto Status => _status;

    /// <summary><c>true</c> when an enhancement pipeline is wired (AI services registered). When
    /// <c>false</c> the endpoint must reject with 503 -- there is nothing to run.</summary>
    public bool IsAvailable => pipeline is not null;

    /// <summary>Raised on each pipeline progress tick. The broadcaster turns it into a WebSocket push.</summary>
    public event EventHandler<EnhanceProgress>? Progressed;

    /// <summary>Raised once when a run finishes (success, failure, or cancellation).</summary>
    public event EventHandler<EnhanceJobCompletedEventArgs>? Completed;

    /// <summary>
    /// Starts an enhance run if none is in flight. Returns <c>false</c> when one is already running
    /// (the caller should respond 409). The work runs on a background task tied to
    /// <paramref name="appStopping"/> (the host's lifetime), NOT the request, so it survives the POST
    /// returning and is cancelled only on server shutdown.
    /// </summary>
    public bool TryStart(EnhanceRequestDto request, EnhanceOptions options, CancellationToken appStopping)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return false;
        }

        var outputPath = ResolveOutputPath(request);
        _status = new EnhanceStatusDto
        {
            IsEnhancing = true,
            InputPath = request.InputPath,
            OutputPath = outputPath,
            Backend = options.Backend.ToString(),
            Succeeded = null,
        };
        _ = Task.Run(() => RunAsync(request.InputPath, outputPath, options, appStopping), appStopping);
        return true;
    }

    private async Task RunAsync(string inputPath, string outputPath, EnhanceOptions options, CancellationToken ct)
    {
        var ok = false;
        string? error = null;
        try
        {
            // Defensive: the endpoint gates on IsAvailable before TryStart, so this should be
            // unreachable. Narrows the nullable pipeline for the rest of the method.
            if (pipeline is not { } pipe)
            {
                error = "AI enhance is not available (no enhancement pipeline registered)";
            }
            else if (!Image.TryReadFitsFile(inputPath, out var src, out var wcs))
            {
                error = $"Failed to read FITS file: {inputPath}";
            }
            else
            {
                // AI enhancers require [0, 1]. ScaleFloatValuesToUnit is a no-op when already
                // normalised and otherwise returns a fresh copy; `src` is left unchanged.
                var normalised = src.ScaleFloatValuesToUnit();

                // BlurX-first program when a deblurrer is registered (RC-Astro), else the SAS-shaped
                // canonical -- the same selection the viewer + MasterPostProcessor make, via the shared
                // SharpenRequest factories (single source of truth for the step program).
                var request = pipe.SupportsDeblur
                    ? SharpenRequest.DeblurFirst(normalised)
                    : SharpenRequest.Canonical(normalised);

                // Synchronous relay (NOT Progress<T>): runs inline on this task thread so the status
                // snapshot updates in order and never overwrites the terminal status set in finally.
                var progress = new SyncProgress(p =>
                {
                    _status = new EnhanceStatusDto
                    {
                        IsEnhancing = true,
                        InputPath = inputPath,
                        OutputPath = outputPath,
                        Backend = options.Backend.ToString(),
                        CurrentStep = p.StepName,
                        StepIndex = p.StepIndex,
                        StepCount = p.StepCount,
                        Percent = OverallPercent(p),
                        Succeeded = null,
                    };
                    Progressed?.Invoke(this, p);
                });

                var result = await pipe.ProcessAsync(request, options, progress, ct).ConfigureAwait(false);

                if (result.Final is { } final)
                {
                    final.WriteToFitsFile(outputPath, wcs);
                    ok = true;
                }
                else
                {
                    error = "Enhance produced no image";
                }

                // Release every plate the orchestrator allocated (mirrors the CLI's cleanup).
                result.Starless?.Release();
                result.StarsOnly?.Release();
                result.SharpenedStars?.Release();
                result.DeconvolvedStarless?.Release();
                result.DenoisedStarless?.Release();
                result.Final?.Release();
            }
        }
        catch (OperationCanceledException)
        {
            error = "Enhance cancelled";
            logger.LogInformation("Image enhance cancelled for {Input}", inputPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogError(ex, "Image enhance failed for {Input}", inputPath);
        }
        finally
        {
            _status = new EnhanceStatusDto
            {
                IsEnhancing = false,
                InputPath = inputPath,
                OutputPath = ok ? outputPath : null,
                Backend = options.Backend.ToString(),
                Percent = ok ? 100f : _status.Percent,
                Error = error,
                Succeeded = ok,
            };
            Interlocked.Exchange(ref _busy, 0);
            Completed?.Invoke(this, new EnhanceJobCompletedEventArgs(inputPath, ok ? outputPath : null, ok, error));
        }
    }

    private static float OverallPercent(EnhanceProgress p)
        => p.StepCount > 0
            ? (p.StepIndex + Math.Clamp(p.StepPercent, 0f, 1f)) / p.StepCount * 100f
            : 0f;

    /// <summary>Explicit output path, or <c>&lt;input&gt;_enhanced.fits</c> next to the input.</summary>
    private static string ResolveOutputPath(EnhanceRequestDto request)
    {
        if (!string.IsNullOrWhiteSpace(request.OutputPath))
        {
            return request.OutputPath;
        }

        var dir = Path.GetDirectoryName(request.InputPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(request.InputPath);
        return Path.Combine(dir, $"{name}_enhanced.fits");
    }

    /// <summary>Inline <see cref="IProgress{T}"/> that reports synchronously on the caller's thread
    /// (unlike <see cref="Progress{T}"/>, which posts to the captured context out of order).</summary>
    private sealed class SyncProgress(Action<EnhanceProgress> sink) : IProgress<EnhanceProgress>
    {
        public void Report(EnhanceProgress value) => sink(value);
    }
}

/// <summary>Payload for <see cref="HostedImageEnhancer.Completed"/>.</summary>
internal sealed class EnhanceJobCompletedEventArgs(string inputPath, string? outputPath, bool succeeded, string? error) : EventArgs
{
    public string InputPath { get; } = inputPath;
    public string? OutputPath { get; } = outputPath;
    public bool Succeeded { get; } = succeeded;
    public string? Error { get; } = error;
}
