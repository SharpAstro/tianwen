using TianWen.Lib.Astrometry;

namespace TianWen.Hosting.Dto;

/// <summary>
/// A plate solution on the wire, whole: the reference point, the CD matrix and the SIP polynomials, so a client draws
/// the same overlay from it as from a solve of its own (P5 part 3 of docs/plans/hardware-in-the-server.md, #934). The
/// pixel frame is <see cref="WCS"/>'s own, 0-based detected-centroid coordinates, never the FITS header's. A value the
/// solution does not have crosses as null, never 0.
/// </summary>
public sealed class WcsDto
{
    /// <summary>J2000 RA of the reference pixel, in hours.</summary>
    public required double CenterRA { get; init; }

    /// <summary>J2000 Dec of the reference pixel, in degrees.</summary>
    public required double CenterDec { get; init; }

    public double? CRPix1 { get; init; }
    public double? CRPix2 { get; init; }
    public double? CD1_1 { get; init; }
    public double? CD1_2 { get; init; }
    public double? CD2_1 { get; init; }
    public double? CD2_2 { get; init; }

    /// <summary>The SIP order; 0 for a linear solution, which carries no polynomials.</summary>
    public int SipOrder { get; init; }

    /// <summary>SIP coefficients as rows (a rectangular array does not cross JSON), each null when absent.</summary>
    public double[][]? SipA { get; init; }
    public double[][]? SipB { get; init; }
    public double[][]? SipAP { get; init; }
    public double[][]? SipBP { get; init; }

    public static WcsDto From(WCS wcs) => new WcsDto
    {
        CenterRA = wcs.CenterRA,
        CenterDec = wcs.CenterDec,
        CRPix1 = Finite(wcs.CRPix1),
        CRPix2 = Finite(wcs.CRPix2),
        CD1_1 = Finite(wcs.CD1_1),
        CD1_2 = Finite(wcs.CD1_2),
        CD2_1 = Finite(wcs.CD2_1),
        CD2_2 = Finite(wcs.CD2_2),
        SipOrder = wcs.SipOrder,
        SipA = Rows(wcs.SipA),
        SipB = Rows(wcs.SipB),
        SipAP = Rows(wcs.SipAP),
        SipBP = Rows(wcs.SipBP),
    };

    public WCS ToWcs() => new WCS(CenterRA, CenterDec)
    {
        CRPix1 = CRPix1 ?? double.NaN,
        CRPix2 = CRPix2 ?? double.NaN,
        CD1_1 = CD1_1 ?? double.NaN,
        CD1_2 = CD1_2 ?? double.NaN,
        CD2_1 = CD2_1 ?? double.NaN,
        CD2_2 = CD2_2 ?? double.NaN,
        SipOrder = SipOrder,
        SipA = Grid(SipA),
        SipB = Grid(SipB),
        SipAP = Grid(SipAP),
        SipBP = Grid(SipBP),
    };

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    private static double[][]? Rows(double[,]? grid)
    {
        if (grid is null)
        {
            return null;
        }
        var rows = new double[grid.GetLength(0)][];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = new double[grid.GetLength(1)];
            for (var j = 0; j < rows[i].Length; j++)
            {
                rows[i][j] = grid[i, j];
            }
        }
        return rows;
    }

    private static double[,]? Grid(double[][]? rows)
    {
        if (rows is null)
        {
            return null;
        }
        var grid = new double[rows.Length, rows.Length == 0 ? 0 : rows[0].Length];
        for (var i = 0; i < rows.Length; i++)
        {
            for (var j = 0; j < rows[i].Length; j++)
            {
                grid[i, j] = rows[i][j];
            }
        }
        return grid;
    }
}

/// <summary>
/// How the frame an OTA showed was solved: <c>GET /api/v1/preview/ota/{index}/solution</c>, written by a solve job or a
/// solve and sync. A job carries no result of its own; this is where a solve's lives.
/// </summary>
public sealed class PlateSolutionDto
{
    /// <summary>The token of the frame solved (the frame routes' <c>X-Frame-Number</c>), so a client knows which it belongs to.</summary>
    public int FrameNumber { get; init; }

    public bool Solved { get; init; }

    /// <summary>What it came to, in words.</summary>
    public required string Message { get; init; }

    public double ElapsedSeconds { get; init; }

    /// <summary>The solution; null when the solve found none.</summary>
    public WcsDto? Solution { get; init; }
}
