using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Launches a native file picker dialog via platform shell commands.
/// The user may select a FITS file or a folder; the caller decides what to do.
/// Returns the selected path, or <c>null</c> if cancelled.
/// </summary>
public static class FileDialogHelper
{
    // PowerShell script for the modern OpenFileDialog.
    // Uses powershell.exe (Windows PowerShell 5.1, always available on Win10/11).
    private const string PsScript = """
        Add-Type -AssemblyName System.Windows.Forms
        $d = [System.Windows.Forms.OpenFileDialog]::new()
        $d.Filter = 'FITS files|*.fits;*.fit;*.fts|All files|*.*'
        $d.Title = 'Open FITS file'
        if ($d.ShowDialog() -eq 'OK') { $d.FileName }
        """;

    public static string? Pick()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return PickWindows();
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RunProcess("zenity", "--file-selection --file-filter='FITS files | *.fits *.fit *.fts' --file-filter='All files | *'")
                ?? RunProcess("kdialog", "--getopenfilename . 'FITS files (*.fits *.fit *.fts)'");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RunProcess("osascript", "-e 'POSIX path of (choose file of type {\"fits\", \"fit\", \"fts\"} with prompt \"Open FITS file\")'");
        }
        return null;
    }

    private static string? PickWindows()
    {
        var tempPs1 = Path.Combine(Path.GetTempPath(), $"tianwen_open_{Environment.ProcessId}.ps1");
        try
        {
            File.WriteAllText(tempPs1, PsScript);
            return RunProcess("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{tempPs1}\"");
        }
        finally
        {
            try { File.Delete(tempPs1); } catch { /* best effort */ }
        }
    }

    private static string? RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
