using Astap.Lib.Imaging;
using nom.tam.fits;
using nom.tam.util;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Astap.Lib.Astrometry.PlateSolve;

public abstract class ExternalProcessPlateSolverBase : IPlateSolver
{
    protected const PlatformID CygwinPlatformId = (PlatformID)('C' << 14 | 'y' << 7 | 'g');

    public abstract string Name { get; }

    protected abstract PlatformID CommandPlatform { get; }

    protected abstract string? CommandFolder { get; }

    protected abstract string CommandFile { get; }

    public abstract float Priority { get; }

    public virtual async Task<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
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
    public async Task<WCS?> SolveFileAsync(
        string fitsFile,
        ImageDim? imageDim = default,
        float range = IPlateSolver.DefaultRange,
        WCS? searchOrigin = null,
        double? searchRadius = null,
        CancellationToken cancellationToken = default
    )
    {
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

        var normalisedFilePath = await NormaliseFilePathAsync(fitsFile, cancellationToken).ConfigureAwait(false);

        var solveFieldArgs = FormatSolveProcessArgs(normalisedFilePath, FormatImageDimenstions(imageDim, range), FormatSearchPosition(searchOrigin, searchRadius));
        var solveFieldProc = StartRedirectedProcess(CommandFile, solveFieldArgs);
        if (solveFieldProc is null)
        {
            return default;
        }

        var outputLines = new ConcurrentQueue<string>();
        solveFieldProc.OutputDataReceived += (sender, e) => { if (e.Data is string data) { outputLines.Enqueue(data); } };
        solveFieldProc.ErrorDataReceived += (sender, e) => { if (e.Data is string data) { outputLines.Enqueue(data); } };

        solveFieldProc.BeginOutputReadLine();
        solveFieldProc.BeginErrorReadLine();

        await solveFieldProc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var axyFile = Path.ChangeExtension(fitsFile, ".axy");
        if (File.Exists(axyFile))
        {
            File.Delete(axyFile);
        }

        var wcsFile = Path.ChangeExtension(fitsFile, ".wcs");
        var hasWCSFile = File.Exists(wcsFile);
        if (solveFieldProc.ExitCode != 0 || !hasWCSFile)
        {
            throw new PlateSolverException($"Failed to solve {normalisedFilePath} file, exit code {solveFieldProc.ExitCode}, has WCS: {hasWCSFile}, log: {string.Join('\n', outputLines)}");
        }

        var wcsReader = new BufferedFile(wcsFile, FileAccess.ReadWrite, FileShare.Read, 1000 * 2088);
        var wcs = new Fits(wcsReader);
        try
        {
            using (wcs.Stream)
            {
                return WCS.FromFits(wcs);
            }
        }
        finally
        {
            File.Delete(wcsFile);
        }
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

        var pathTranslateProc = CommandPlatform == CygwinPlatformId
            ? StartRedirectedProcess("cygpath", $"\"{fitsFile}\"", executionPlatform: PlatformID.Win32NT)
            : StartRedirectedProcess("wslpath", $"\"{fitsFile}\"");
        if (pathTranslateProc is null)
        {
            throw new PlateSolverException($"Failed to start process for {fitsFile}");
        }

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
