using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The live view's Derive (#1159, the owner's choice of 2026-10-02): the gains the derived sharpening would use for the master on show
/// (<see cref="PlanetaryBestStack.DeriveGains"/>), worked out in the background, about 35 s, and seeded into the wavelet sliders, which then
/// sharpen every later master with them at the cost of a wavelet pass. ONE for the viewer's stacked view of a SER and the GUI's planetary
/// capture, so the two derive alike. Seeded sliders sharpen as the batch's floored derived sharpening does, measured equal on every twin
/// (docs/plans/planetary-restoration.md, "The live view's derived sharpening"), and the limb the derivation fitted is kept
/// (<see cref="ViewerState.WaveletLimb"/>), so every later master is drawn outside it as the batch draws it (#1201).
/// </summary>
internal sealed class WaveletDerivation : IDisposable
{
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private Task<DerivedGains>? _task;
    private int _disposed;

    /// <summary>How many derivations have started; switching views and stops starts none (#1314 rule 7). For tests.</summary>
    internal int Started { get; private set; }

    /// <summary>
    /// Render thread, each tick of a live view: applies a finished derivation to <paramref name="state"/> (its gains to the sliders, or the
    /// reason there are none), then starts one over <paramref name="source"/>'s master when the panel asked and none runs. The planet and a
    /// mono capture's filter are the panel's, else <paramref name="capturePath"/>'s name's; the telescope is the panel's (the GUI seeds it
    /// from the profile); the instant is the master's frame's time, else <paramref name="now"/> (a live capture is now).
    /// </summary>
    public void Tick(ViewerState state, LiveStackPreviewSource? source, string? capturePath, DateTimeOffset now, ILogger logger)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        if (_task is { IsCompleted: true } done)
        {
            _task = null;
            state.WaveletDeriving = false;
            if (done.IsCompletedSuccessfully && done.Result is { } derived)
            {
                if (derived.Gains.IsDefaultOrEmpty)
                {
                    state.WaveletDeriveNote = $"Not derived: {derived.How}";
                }
                else
                {
                    // The stop on show now, which a press during the derivation may have moved (#1314).
                    state.ShowDerivedAt(derived, state.PlanetaryStrength);
                }
            }
            else if (done.IsFaulted)
            {
                logger.LogWarning(done.Exception?.GetBaseException(), "Deriving the wavelet gains failed");
                state.WaveletDeriveNote = $"Not derived: {done.Exception?.GetBaseException().Message}";
            }
            state.NeedsRedraw = true;
        }

        if (!state.WaveletDeriveRequested)
        {
            return;
        }
        state.WaveletDeriveRequested = false;
        if (_task is not null)
        {
            return;
        }
        // A lease, so the master stays readable however the source moves on while the derivation runs.
        if (source?.RawMaster is not { } raw || !raw.TryLease(out var lease))
        {
            state.WaveletDeriveNote = "Not derived: no stacked master yet";
            state.NeedsRedraw = true;
            return;
        }

        // A planetary master's planet is its OBJECT's (#1314), a capture's its name's.
        var planet = state.PlanetaryBody ?? state.MasterPlanet ?? (capturePath is null ? null : PlanetaryCaptureName.Planet(capturePath));
        var pupil = PlanetaryBestStack.PupilFor(state.PlanetaryApertureMm, state.PlanetaryDesign);
        var filterNm = state.PlanetaryFilterNm ?? (capturePath is null ? null : PlanetaryCaptureName.WavelengthNm(capturePath));
        // A filter is one wavelength for every channel, so only a mono master takes it; a colour one is derived per channel.
        ImmutableArray<double> wavelengths = filterNm is { } nm && raw.ChannelCount == 1 ? [nm] : [];
        var frameTime = source.HasTimestamps ? source.TimestampOf(source.FrameIndex) : DateTimeOffset.MinValue;
        var epoch = frameTime > DateTimeOffset.MinValue ? frameTime : now;
        var strength = state.PlanetaryStrength;

        state.WaveletDeriving = true;
        state.NeedsRedraw = true;
        Started++;
        var token = _cts.Token;
        _task = Task.Run(() =>
        {
            using (lease)
            {
                token.ThrowIfCancellationRequested();
                // Every stop's gains in the one derivation, so a stop chosen after it switches the dials at once (#1314).
                return PlanetaryBestStack.DeriveGains(lease.Image, planet, epoch, pupil, wavelengths, strength: strength,
                    stops: PlanetarySharpening.StrengthStops);
            }
        }, token);
    }

    /// <summary>
    /// A derivation running is abandoned: its result is never applied. Safe to call again, as a host disposes its capture controller
    /// both itself and through its service scope (the functional suite's GUI harness did, and the second cancel threw).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cts.Cancel();
        _cts.Dispose();
    }
}
