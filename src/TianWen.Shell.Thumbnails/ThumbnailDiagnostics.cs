using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TianWen.Shell.Thumbnails
{
    /// <summary>
    /// Why a thumbnail was not produced, written somewhere a person can read afterwards.
    /// </summary>
    /// <remarks>
    /// <para>This handler runs inside the shell's COM surrogate (dllhost.exe) under the package
    /// identity: no console, no stderr, nobody watching, and an HRESULT returned to the shell becomes a
    /// generic file icon with the reason discarded. So a failure that reproduces on a user's machine and
    /// nowhere else has, until now, been unreadable.</para>
    ///
    /// <para><b>Failures only.</b> A thumbnail that works writes nothing, so browsing a folder of a
    /// thousand frames costs nothing. That also makes the SILENCE diagnostic: if a file draws no
    /// thumbnail and this log gains no line, the handler was never activated at all, and the problem is
    /// registration or the surrogate rather than anything in here. Distinguishing those two was the
    /// reason this was written (2026-09-13: FITS on a OneDrive path fails while the same bytes copied
    /// locally succeed, and IShellItemImageFactory reports 0x8004b205 in 23 ms, far too fast to be a
    /// hydration timeout).</para>
    ///
    /// <para><b>A TIFF drawing correctly beside a FITS that does not proves less than it looks.</b>
    /// Windows renders TIFF through an in-box WIC codec loaded IN-PROCESS; a packaged handler like this
    /// one is only ever allowed to run out-of-process in the surrogate. The two take different paths to
    /// the same bytes, so the TIFF succeeding says the shell can read the file, not that a surrogate
    /// can.</para>
    ///
    /// <para>Two sinks, because they fail in different situations: <c>OutputDebugString</c> is free,
    /// needs no write access and shows up live in DebugView or a debugger, while the file survives the
    /// session for someone who was not watching. Under MSIX the file lands in the package's redirected
    /// LocalCache rather than the real <c>%LOCALAPPDATA%</c>; <see cref="LogPath"/> is resolved the same
    /// way the writer resolves it, so a caller can just ask.</para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    internal static partial class ThumbnailDiagnostics
    {
        /// <summary>Past this the file is started again, so a stuck failure cannot fill a disk.</summary>
        private const long MaxBytes = 256 * 1024;

        [LibraryImport("kernel32.dll", EntryPoint = "OutputDebugStringW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial void OutputDebugString(string message);

        /// <summary>Where <see cref="Failure"/> writes, or <c>null</c> when even that cannot be resolved.</summary>
        internal static string? LogPath
        {
            get
            {
                try
                {
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    return local.Length == 0
                        ? null
                        : Path.Combine(local, "TianWen", "Logs", "thumbnail-diagnostics.log");
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Records one failed step. <paramref name="hr"/> is the HRESULT about to be returned to the
        /// shell, which is the value a caller comparing against <c>IShellItemImageFactory</c> needs.
        /// </summary>
        /// <remarks>
        /// Best effort throughout and never throws: a shell extension that fails while explaining itself
        /// is worse than one that cannot explain itself, and this sits on the path that is already
        /// failing.
        /// </remarks>
        internal static void Failure(string step, int hr, string? detail = null)
        {
            string line;
            try
            {
                line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ} pid={Environment.ProcessId} {step} hr=0x{hr:x8}{(detail is { Length: > 0 } d ? " " + d : "")}");
            }
            catch
            {
                return;
            }

            try
            {
                OutputDebugString("[tianwen-thumb] " + line);
            }
            catch
            {
                // A missing kernel32 export is not a thing that happens, but neither is this worth a throw.
            }

            try
            {
                if (LogPath is not { Length: > 0 } path)
                {
                    return;
                }

                var dir = Path.GetDirectoryName(path);
                if (dir is { Length: > 0 })
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Delete(path);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // No write access from the surrogate is itself plausible here, and there is nowhere left
                // to report it. OutputDebugString above has already carried the line to anyone watching.
            }
        }
    }
}
