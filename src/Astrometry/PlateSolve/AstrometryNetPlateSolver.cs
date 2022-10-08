using System;
using System.Text;

namespace Astap.Lib.Astrometry.PlateSolve;

public class AstrometryNetPlateSolver : ExternalProcessPlateSolverBase
{
    public AstrometryNetPlateSolver() { }

    public override string Name => "Astromentry.NET plate solver";

    protected override PlatformID CommandPlatform => PlatformID.Unix;

    protected override string CommandPath => "solve-field";

    protected override string FormatSearchPosition((double ra, double dec)? searchOrigin, double? searchRadius)
    {
        if (!searchOrigin.HasValue)
        {
            return "";
        }

        var sb = new StringBuilder(30);
        sb.AppendFormat("--ra {0:0.#######} --dec \"{1:0.#######}\"", searchOrigin.Value.ra, searchOrigin.Value.dec);
        if (searchRadius is double radius)
        {
            sb.AppendFormat(" --radius {0.##}", radius);
        }
        return sb.ToString();
    }

    protected override string FormatSolveProcessArgs(string normalisedFilePath, string pixelScaleFmt, string searchPosFmt)
        => $"\"{normalisedFilePath}\" --no-plots --overwrite {pixelScaleFmt} {searchPosFmt} -N none -U none -S none -B none -M none -R none --axy none --temp-axy";

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
