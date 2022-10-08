using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Astap.Lib.Astrometry.PlateSolve;


public abstract class ExternalProcessPlateSolverBase : IPlateSolver
{
    public abstract string Name { get; }

    protected abstract PlatformID CommandPlatform { get; }

    protected abstract string CommandPath { get; }

    public virtual async Task<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var proc = StartRedirectedProcess(CommandPath, "-h");
            if (proc is null)
            {
                return false;
            }
            await proc.WaitForExitAsync(cancellationToken);

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(double ra, double dec)?> SolveFileAsync(
        string fitsFile,
        ImageDim? imageDim = default,
        float range = IPlateSolver.DefaultRange,
        (double ra, double dec)? searchOrigin = null,
        double? searchRadius = null,
        CancellationToken cancellationToken = default
    )
    {
        if (imageDim is ImageDim dim)
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
        }
        if (range <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(range), range, "Range must be greater than 0");
        }
        if (range > imageDim?.PixelScale)
        {
            throw new ArgumentOutOfRangeException(nameof(range), range, "Range must be smaller than pixel scale");
        }

        var normalisedFilePath = await NormaliseFilePathAsync(fitsFile, cancellationToken);
        if (normalisedFilePath is null)
        {
            return default;
        }

        var solveFieldArgs = FormatSolveProcessArgs(normalisedFilePath, FormatImageDimenstions(imageDim, range), FormatSearchPosition(searchOrigin, searchRadius));
        var solveFieldProc = StartRedirectedProcess(CommandPath, solveFieldArgs);
        if (solveFieldProc is null)
        {
            return default;
        }

        await solveFieldProc.WaitForExitAsync(cancellationToken);

        return default;
    }

    protected abstract string FormatImageDimenstions(ImageDim? imageDim, float range);

    protected abstract string FormatSearchPosition((double ra, double dec)? searchOrigin, double? searchRadius);

    protected abstract string FormatSolveProcessArgs(string normalisedFilePath, string pixelScaleFmt, string searchPosFmt);

    protected virtual Process? StartRedirectedProcess(string proc, string arguments)
    {
        switch (Environment.OSVersion.Platform)
        {
            case PlatformID.Win32NT:
                var startInfo = CommandPlatform switch
                {
                    PlatformID.Win32NT => new ProcessStartInfo(proc, arguments)
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
                };
                return Process.Start(startInfo);

            case PlatformID.Unix:
                return Process.Start(proc, arguments);

            default:
                return null;
        }
    }

    protected virtual async Task<string?> NormaliseFilePathAsync(string fitsFile, CancellationToken cancellationToken = default)
    {
        // if we are not on Windows or the command is a native windows command
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || CommandPlatform == PlatformID.Win32NT)
        {
            return fitsFile;
        }

        var wslPathProc = StartRedirectedProcess("wslpath", $"\"{fitsFile}\"");
        if (wslPathProc is null)
        {
            return default;
        }

        var sb = new StringBuilder(40);
        wslPathProc.OutputDataReceived += (sender, e) => { if (e.Data is string data) { sb.Append(data); } };

        await wslPathProc.WaitForExitAsync(cancellationToken);

        if (wslPathProc.ExitCode == 0 && sb.Length > 0)
        {
            return sb.ToString().Trim();
        }
        else
        {
            return default;
        }
    }
}
