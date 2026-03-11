using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TianWen.UI.OpenGL;

[SupportedOSPlatform("windows")]
public static partial class WindowHelper
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE = 20
    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref int pvAttribute, uint cbAttribute);

    public static void EnableDarkTitleBar(nint hwnd)
    {
        var useDarkMode = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
    }
}
