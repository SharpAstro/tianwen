using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Copies the NEXT session's raw lights onto the scratch disk while the current one bakes, so a
/// session's many passes over its lights (measure, warp, and the drizzle's two) read a fast local
/// copy instead of the archive disk, and the one slow read of the archive overlaps work already
/// running.
/// </summary>
/// <remarks>
/// <para><b>Why:</b> on this archive the lights live on a USB hard disk, where a cold 18 MB frame
/// takes 329 ms to load and a warm one 38 ms (the DrizzleCostProbe, 2026-09-24), and the bake reads
/// every light at least four times. The OS file cache catches most of the repeats while it holds a
/// session, but not the first read, and not at all once a large session outgrows it. A sequential
/// copy of session k+1 runs at the disk's streaming rate while session k is busy on the CPU.</para>
/// <para><b>Only the read is redirected</b> (<see cref="FrameInfo.StagedPath"/>): every record keeps
/// naming the archive file. A copy is written under a temporary name and renamed into place, so a
/// half-copied file is never read; any light whose copy fails is simply read from the archive.</para>
/// <para><b>Bounded:</b> at most two sessions are staged at once (the one baking and the next), and a
/// session is not staged at all when its lights would not fit beside the reserve, or when they already
/// live on the scratch volume, where a copy buys nothing.</para>
/// </remarks>
/// <param name="stageRoot">Directory the copies go under; created and removed by this class alone.</param>
/// <param name="willProcess">Whether the bake will actually read a session's lights (a session a resume
/// skips is not worth copying).</param>
public sealed class SessionStager(string stageRoot, Func<ImagingSession, bool> willProcess, ILogger? logger = null) : IAsyncDisposable
{
    /// <summary>Stage even lights that already share the scratch volume. Tests only: a temp directory
    /// and its stage root are always on one volume, which production correctly declines to copy.</summary>
    internal bool StageSameVolume { get; init; }

    /// <summary>Free space always left on the scratch volume after a copy.</summary>
    public const long ReserveBytes = 20L << 30;

    private const int CopyBufferBytes = 1 << 20;

    private readonly Dictionary<int, Staging> _staged = [];

    /// <summary>
    /// Called at the top of each session: releases the previous session's copy, waits for this one's
    /// (started while the previous session baked), then starts copying the next. Returns the session
    /// with its lights redirected to their copies, or <paramref name="sessions"/>[<paramref name="index"/>]
    /// unchanged when it was not staged.
    /// </summary>
    public async Task<ImagingSession> EnterAsync(IReadOnlyList<ImagingSession> sessions, int index, CancellationToken ct)
    {
        if (_staged.Remove(index - 1, out var previous))
        {
            await previous.DisposeAsync();
        }

        var session = sessions[index];
        if (_staged.TryGetValue(index, out var mine))
        {
            session = await mine.Ready;
        }

        // After this session's copy is complete, never alongside it: two copies at once would only
        // share the archive disk and finish the one needed now later.
        if (index + 1 < sessions.Count && willProcess(sessions[index + 1]))
        {
            _staged[index + 1] = Start(sessions[index + 1], index + 1, ct);
        }

        return session;
    }

    private Staging Start(ImagingSession session, int index, CancellationToken ct)
    {
        var dir = Path.Combine(stageRoot, $"{index:D4}_{ShortHash(session.Id)}");
        return new Staging(dir, ct, token => CopyAsync(session, dir, token), logger);
    }

    private async Task<ImagingSession> CopyAsync(ImagingSession session, string dir, CancellationToken ct)
    {
        try
        {
            var sizes = session.Lights.Select(l => new FileInfo(l.Path)).ToArray();
            var need = sizes.Where(f => f.Exists).Sum(f => f.Length);
            Directory.CreateDirectory(dir);
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (!StageSameVolume
                && session.Lights.All(l => string.Equals(Path.GetPathRoot(Path.GetFullPath(l.Path)), root, StringComparison.OrdinalIgnoreCase)))
            {
                logger?.LogInformation("  [{Session}] lights already on the scratch volume; not staged", session.Id);
                return session;
            }

            var free = new DriveInfo(root ?? dir).AvailableFreeSpace;
            if (need > free - ReserveBytes)
            {
                logger?.LogInformation(
                    "  [{Session}] not staged: {Need:F1} GB of lights against {Free:F1} GB free on the scratch volume (reserve {Reserve} GB)",
                    session.Id, need / 1e9, free / 1e9, ReserveBytes >> 30);
                return session;
            }

            var started = DateTime.UtcNow;
            var staged = ImmutableArray.CreateBuilder<FrameInfo>(session.Lights.Length);
            var copied = 0;
            for (var i = 0; i < session.Lights.Length; i++)
            {
                var light = session.Lights[i];
                var target = Path.Combine(dir, $"{i:D5}_{Path.GetFileName(light.Path)}");
                try
                {
                    await CopyOneAsync(light.Path, target, ct);
                    staged.Add(light with { StagedPath = target });
                    copied++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Read from the archive instead: staging is an acceleration, never a dependency.
                    logger?.LogWarning(ex, "  [{Session}] staging {File} failed; it is read from the archive", session.Id, light.Path);
                    staged.Add(light);
                }
            }

            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            logger?.LogInformation(
                "  [{Session}] staged {Copied}/{Lights} lights ({Size:F1} GB) on the scratch volume in {Seconds:F0} s ({Rate:F0} MB/s)",
                session.Id, copied, session.Lights.Length, need / 1e9, seconds, need / 1e6 / Math.Max(seconds, 1e-3));
            return session with { Lights = staged.MoveToImmutable() };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Anything but a cancellation: staging is an acceleration, so the session goes ahead on
            // its archive files rather than failing for it.
            logger?.LogWarning(ex, "  [{Session}] staging failed; its lights are read from the archive", session.Id);
            return session;
        }
    }

    private static async Task CopyOneAsync(string source, string target, CancellationToken ct)
    {
        var partial = target + ".part";
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes,
            FileOptions.Asynchronous))
        {
            output.SetLength(input.Length);
            await input.CopyToAsync(output, CopyBufferBytes, ct);
        }
        File.Move(partial, target, overwrite: true);
    }

    private static string ShortHash(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    /// <summary>Cancels any copy still running and removes every staged file.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var staging in _staged.Values)
        {
            await staging.DisposeAsync();
        }
        _staged.Clear();
        TryDeleteDirectory(stageRoot, logger);
    }

    private static void TryDeleteDirectory(string dir, ILogger? logger)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "could not remove staged lights under {Dir}", dir);
        }
    }

    /// <summary>One session's copy: its directory, the copy task, and the means to stop it. It owns
    /// its cancellation source, created here and disposed in <see cref="DisposeAsync"/>.</summary>
    private sealed class Staging : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly CancellationTokenSource _cts;
        private readonly ILogger? _logger;

        public Staging(string dir, CancellationToken ct, Func<CancellationToken, Task<ImagingSession>> copy, ILogger? logger)
        {
            _dir = dir;
            _logger = logger;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            Ready = Task.Run(() => copy(token), CancellationToken.None);
        }

        public Task<ImagingSession> Ready { get; }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await Ready;
            }
            catch (OperationCanceledException ex)
            {
                // Expected: the copy was cancelled because nothing will read it now.
                _logger?.LogDebug(ex, "staging under {Dir} cancelled", _dir);
            }
            _cts.Dispose();
            TryDeleteDirectory(_dir, _logger);
        }
    }
}
