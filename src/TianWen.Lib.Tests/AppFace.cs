using System;
using System.IO;

namespace TianWen.Lib.Tests;

/// <summary>
/// The one home of the text face the apps draw: BundledFonts' DejaVu Sans, the same file on every platform, copied beside
/// the test binary as TestFonts/DejaVuSans.ttf. A test that judges text measures in this face, never in the platform's
/// monospace default (Consolas on Windows, DejaVu Sans Mono on Linux CI), so a Windows run sees what CI sees (#2560).
/// </summary>
internal static class AppFace
{
    internal static string Text { get; } = Path.Combine(AppContext.BaseDirectory, "TestFonts", "DejaVuSans.ttf");
}
