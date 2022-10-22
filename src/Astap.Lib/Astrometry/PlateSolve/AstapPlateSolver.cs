using Astap.Lib.Imaging;
using System;
using System.IO;
using System.Text;

namespace Astap.Lib.Astrometry.PlateSolve;

public class AstapPlateSolver : ExternalProcessPlateSolverBase
{
    public override string Name => "ASTAP Plate Solver";

    protected override PlatformID CommandPlatform => Environment.OSVersion.Platform;

    protected override string? CommandFolder => CommandPlatform == PlatformID.Win32NT
        ? Path.Combine(Environment.ExpandEnvironmentVariables("%ProgramW6432%"), "astap")
        : null;

    protected override string CommandFile => CommandPlatform == PlatformID.Win32NT ? "astap_cli.exe" : "astap";

    protected override string FormatImageDimenstions(ImageDim? imageDim, float range)
    {
        const float ScaleFactor = 0.007f / IPlateSolver.DefaultRange;

        var sb = new StringBuilder(30);
        if (imageDim is ImageDim dim)
        {
            sb.AppendFormat(" -fov {0:0.000}", dim.FieldOfView.height);
        }
        sb.AppendFormat(" -t {0:0.000}", range * ScaleFactor);
        return sb.ToString();
    }

    protected override string FormatSearchPosition((double ra, double dec)? searchOrigin, double? searchRadius)
    {
        if (!searchOrigin.HasValue)
        {
            return "";
        }
        (var ra, var dec) = searchOrigin.Value;

        var sb = new StringBuilder(40).AppendFormat(" -ra {0:0.0000} -spd {1:0.0000}", Math.Clamp(ra / 15.0, 0, 24), Math.Clamp(dec + 90.0, 0.0, 180.0));

        if (searchRadius is double radius)
        {
            sb.AppendFormat(" -r {0}", radius);
        }
        return sb.ToString();
    }

    protected override string FormatSolveProcessArgs(string normalisedFilePath, string pixelScaleFmt, string searchPosFmt)
        => $"-f \"{normalisedFilePath}\" {pixelScaleFmt} {searchPosFmt} -wcs";
}
