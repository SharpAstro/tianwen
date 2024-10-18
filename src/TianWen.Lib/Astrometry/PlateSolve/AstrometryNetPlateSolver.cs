using Astap.Lib.Imaging;
using System;
using System.IO;

namespace Astap.Lib.Astrometry.PlateSolve;

internal abstract class AstrometryNetPlateSolver : ExternalProcessPlateSolverBase
{
    private readonly string? _commandFolder;
    private readonly PlatformID _commandPlatform;

    public AstrometryNetPlateSolver(bool supportCygwin)
    {
        var maybeLocalAstrometryInstall = supportCygwin && Environment.OSVersion.Platform == PlatformID.Win32NT
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Astrometry", "bin")
            : null;
        if (!string.IsNullOrEmpty(maybeLocalAstrometryInstall) && Directory.Exists(maybeLocalAstrometryInstall))
        {
            _commandFolder = maybeLocalAstrometryInstall;
            _commandPlatform = CygwinPlatformId;
        }
        else
        {
            // try use WSL / Unix native version instead
            _commandFolder = null;
            _commandPlatform = PlatformID.Unix;
        }
    }

    public override string Name => "Astromentry.NET plate solver";

    protected override PlatformID CommandPlatform => _commandPlatform;

    protected override string? CommandFolder => _commandFolder;

    protected override string CommandFile => "solve-field";

    protected override string FormatSearchPosition(WCS? searchOrigin, double? searchRadius)
        => searchOrigin is (double ra, double dec) && searchRadius is double radius
            ? $"--ra {ra * 15.0:0.######} --dec \"{dec:0.######}\" --radius {radius:0.##}"
            : "";

    protected override string FormatSolveProcessArgs(string normalisedFilePath, string pixelScaleFmt, string searchPosFmt)
        => $"\"{normalisedFilePath}\" --no-plots --overwrite {pixelScaleFmt} {searchPosFmt}" +
            " -N none -U none -S none -B none -M none -R none --crpix-center " +
            (CommandPlatform == PlatformID.Unix ? "--axy none --temp-axy" : "--no-fits2fits");

    protected override string FormatImageDimenstions(ImageDim? imageDim, float range)
    {
        if (imageDim is ImageDim dim)
        {
            var low = dim.PixelScale - range;
            var high = dim.PixelScale + range;
            var pixelScaleFmt = $"-u app -L {low:0.##} -H {high:0.##}";
            return pixelScaleFmt;
        }
        return "";
    }
}
