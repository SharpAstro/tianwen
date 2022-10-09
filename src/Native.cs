using System.Runtime.InteropServices;

namespace Astap.Lib;

internal static class Native
{
    const string astap_lib = "astap_lib";

    [DllImport(astap_lib, CharSet = CharSet.Unicode, SetLastError = false, CallingConvention = CallingConvention.Cdecl)]
    public static extern int analyse_fits(
        string fits,
        double snr_min,
        int max_stars,
        out double medianHFD,
        out double medianFWHM,
        out double background
    );
}
