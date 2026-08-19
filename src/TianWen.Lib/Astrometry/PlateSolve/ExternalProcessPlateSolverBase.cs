using TianWen.Lib.Imaging;
using nom.tam.fits;
using nom.tam.util;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Astrometry.PlateSolve;

public abstract class ExternalProcessPlateSolverBase : IPlateSolver
{
    protected const PlatformID CygwinPlatformId = (PlatformID)('C' << 14 | 'y' << 7 | 'g');

    public abstract string Name { get; }

    protected abstract PlatformID CommandPlatform { get; }

    protected abstract string? CommandFolder { get; }

    protected abstract string CommandFile { get; }

    public abstract float Priority { get; }

    public virtual async ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var proc = StartRedirectedProcess(CommandFile, "-h");
            if (proc is null)
            {
                return false;
            }
            proc.BeginOutputReadLine();
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<PlateSolveResult> SolveFileAsync(
        string fitsFile,
        ImageDim? imageDim = default,
        float range = IPlateSolver.DefaultRange,
        WCS? searchOrigin = null,
        double? searchRadius = null,
        CancellationToken cancellationToken = default
    )
    {
        var sw = Stopwatch.StartNew();
        if (imageDim is { } dim)
        {
            if (dim.PixelScale <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(imageDim), dim.PixelScale, "Pixel scale must be greater than 0");
            }
            if (dim.Width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(imageDim), dim.Width, "Image width  must be greater than 0");
            }
            if (dim.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(imageDim), dim.Height, "Image height  must be greater than 0");
            }
            if (range > dim.PixelScale)
            {
                throw new ArgumentOutOfRangeException(nameof(range), range, "Range must be smaller than pixel scale");
            }
        }
        if (range is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(range), range, "Range must be greater than 0");
        }

        // An external solver opens the file itself, so it can only be handed a format it understands.
        // ASTAP reads plain FITS; a tile-compressed .fz or a .tif is not plain FITS, and handing one
        // over produces a generic non-zero exit that reads like "could not solve this field" rather
        // than "could not open this file" -- which is how a .fz master looked like an unsolvable frame.
        var (solveFilePath, isConverted) = await MaterialiseSolvableFileAsync(fitsFile, cancellationToken).ConfigureAwait(false);
        try
        {
            var normalisedFilePath = await NormaliseFilePathAsync(solveFilePath, cancellationToken).ConfigureAwait(false);

            var solveFieldArgs = FormatSolveProcessArgs(normalisedFilePath, FormatImageDimenstions(imageDim, range), FormatSearchPosition(searchOrigin, searchRadius));
            var solveFieldProc = StartRedirectedProcess(CommandFile, solveFieldArgs);
            if (solveFieldProc is null)
            {
                return new PlateSolveResult(null, sw.Elapsed);
            }

            var outputLines = new ConcurrentQueue<string>();
            solveFieldProc.OutputDataReceived += (sender, e) => { if (e.Data is string data) { outputLines.Enqueue(data); } };
            solveFieldProc.ErrorDataReceived += (sender, e) => { if (e.Data is string data) { outputLines.Enqueue(data); } };

            solveFieldProc.BeginOutputReadLine();
            solveFieldProc.BeginErrorReadLine();

            await solveFieldProc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // Derived from the file the solver was actually GIVEN. The tool writes its sidecars beside
            // its input, so deriving them from the original would look for them next to a file the solver
            // never saw whenever a conversion happened.
            var axyFile = Path.ChangeExtension(solveFilePath, ".axy");
            if (File.Exists(axyFile))
            {
                File.Delete(axyFile);
            }

            var wcsFile = Path.ChangeExtension(solveFilePath, ".wcs");
            var hasWCSFile = File.Exists(wcsFile);
            if (solveFieldProc.ExitCode != 0 || !hasWCSFile)
            {
                throw new PlateSolverException($"Failed to solve {normalisedFilePath} file, exit code {solveFieldProc.ExitCode}, has WCS: {hasWCSFile}, log: {string.Join('\n', outputLines)}");
            }

            try
            {
                using var wcsReader = new BufferedFile(wcsFile, FileAccess.ReadWrite, FileShare.Read, 1000 * 2088);
                using var wcs = new Fits(wcsReader);
                return new PlateSolveResult(WCS.FromFits(wcs), sw.Elapsed);
            }
            finally
            {
                File.Delete(wcsFile);
            }
        }
        finally
        {
            if (isConverted && File.Exists(solveFilePath))
            {
                File.Delete(solveFilePath);
            }
        }
    }

    /// <summary>
    /// Extensions this tool can open directly. Anything else is converted to a plain FITS first.
    /// </summary>
    /// <remarks>
    /// Deliberately the conservative set. Over-claiming costs a confusing failure (the tool exits
    /// non-zero with no useful message), while under-claiming costs one temp file and a write, so the
    /// asymmetry says to list only what is certain.
    /// </remarks>
    protected virtual string[] NativeFileExtensions => [".fits", ".fit", ".fts"];

    /// <summary>
    /// Returns a path the external tool can open, converting the input if necessary, and whether the
    /// result is a temporary that the caller must delete.
    /// </summary>
    /// <remarks>
    /// Conversion rather than refusal: a .fz master IS solvable -- TianWen reads it, and the catalog
    /// solver solves it -- so declining it here would remove a capability to fix a bug. It mirrors
    /// <c>IPlateSolver.SolveImageAsync</c>, which already writes a temp FITS for exactly this reason.
    /// </remarks>
    protected virtual async Task<(string Path, bool IsConverted)> MaterialiseSolvableFileAsync(
        string inputFile, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(inputFile).ToLowerInvariant();
        if (Array.IndexOf(NativeFileExtensions, ext) >= 0)
        {
            return (inputFile, false);
        }

        if (!Image.TryReadImageFile(inputFile, out var image))
        {
            throw new PlateSolverException(
                $"{Name} cannot read {ext} and it could not be converted: {inputFile}");
        }

        var converted = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".fits");
        await Task.Run(() => image.WriteToFitsFile(converted), cancellationToken).ConfigureAwait(false);
        return (converted, true);
    }

    protected abstract string FormatImageDimenstions(ImageDim? imageDim, float range);

    protected abstract string FormatSearchPosition(WCS? searchOrigin, double? searchRadius);

    protected abstract string FormatSolveProcessArgs(string normalisedFilePath, string pixelScaleFmt, string searchPosFmt);

    protected virtual Process? StartRedirectedProcess(string proc, string arguments, PlatformID? executionPlatform = default)
    {
        var startInfo = Environment.OSVersion.Platform switch
        {
            PlatformID.Win32NT => (executionPlatform ?? CommandPlatform) switch
            {
                PlatformID.Win32NT => NativeRedirectedProcessStartInfo(proc, arguments),

                CygwinPlatformId => new ProcessStartInfo(FullNativeCmdPath("bash"), string.Concat("-l -c \"", CommandFile, " ", arguments, "\""))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },

                _ => new ProcessStartInfo("wsl", string.Concat(proc, " ", arguments))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            },

            _ => NativeRedirectedProcessStartInfo(proc, arguments)
        };

        return Process.Start(startInfo);
    }

    ProcessStartInfo NativeRedirectedProcessStartInfo(string proc, string arguments) => new(FullNativeCmdPath(proc), arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    protected virtual async Task<string> NormaliseFilePathAsync(string fitsFile, CancellationToken cancellationToken = default)
    {
        // if we are not on Windows or the command is a native windows command or cygwin
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || CommandPlatform == PlatformID.Win32NT)
        {
            return fitsFile;
        }

        var pathTranslateProc = (
            CommandPlatform == CygwinPlatformId
                ? StartRedirectedProcess("cygpath", $"\"{fitsFile}\"", executionPlatform: PlatformID.Win32NT)
                : StartRedirectedProcess("wslpath", $"\"{fitsFile}\"")
            )
            ?? throw new PlateSolverException($"Failed to start process for {fitsFile}");

        string? line = null;
        var errorLog = new ConcurrentQueue<string>();
        pathTranslateProc.OutputDataReceived += (sender, e) => { if (e.Data is string data && !string.IsNullOrWhiteSpace(data)) { _ = Interlocked.CompareExchange(ref line, data, null); } };
        pathTranslateProc.ErrorDataReceived += (sender, e) => { if (e.Data is string data && !string.IsNullOrWhiteSpace(data)) { errorLog.Enqueue(data); } };
        pathTranslateProc.BeginOutputReadLine();
        pathTranslateProc.BeginErrorReadLine();

        await pathTranslateProc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (pathTranslateProc.ExitCode == 0)
        {
            if (line?.Trim() is string trimmed)
            {
                return trimmed;
            }
            else
            {
                throw new PlateSolverException($"Translating {fitsFile} path failed as no output was received: {string.Join('\n', errorLog)}");
            }
        }
        else
        {
            throw new PlateSolverException($"Translating {fitsFile} path failed with error {pathTranslateProc.ExitCode}, error log: {string.Join('\n', errorLog)}");
        }
    }

    string FullNativeCmdPath(string cmd) =>
        CommandFolder is string folder && !string.IsNullOrEmpty(folder) && Directory.Exists(folder)
            ? Path.Combine(folder, cmd)
            : cmd;
}
