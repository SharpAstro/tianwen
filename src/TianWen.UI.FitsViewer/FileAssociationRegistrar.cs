using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace TianWen.UI.FitsViewer
{
    internal static partial class FileAssociationRegistrar
    {
        private const string ProgId = "TianWen.FitsViewer";
        private const string AppName = "TianWen FITS Image Viewer";

        private static readonly Dictionary<string, string[]> ExtensionGroups = new(StringComparer.OrdinalIgnoreCase)
        {
            ["FITS"] = [".fit", ".fits", ".fts"]
        };

        internal static int Register(string group, ILogger logger)
        {
            if (!ExtensionGroups.TryGetValue(group, out var extensions))
            {
                logger.LogError("Unknown extension group: {Group}. Supported: {Supported}", group, string.Join(", ", ExtensionGroups.Keys));
                return 1;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)
                || Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("--register must be run from the published executable, not via 'dotnet run'");
                return 1;
            }

            if (OperatingSystem.IsWindows())
            {
                return RegisterWindows(exePath, extensions, logger);
            }

            if (OperatingSystem.IsLinux())
            {
                return RegisterLinux(exePath, extensions, logger);
            }

            if (OperatingSystem.IsMacOS())
            {
                logger.LogWarning("macOS file association requires an app bundle. Register manually in Finder via Get Info > Open With.");
                return 1;
            }

            logger.LogError("File association registration is not supported on this platform");
            return 1;
        }

        [SupportedOSPlatform("windows")]
        private static int RegisterWindows(string exePath, string[] extensions, ILogger logger)
        {
            try
            {
                // Per-user registration under HKCU\Software\Classes (no admin required)
                using var classesKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
                if (classesKey is null)
                {
                    logger.LogError(@"Cannot open HKCU\Software\Classes for writing");
                    return 1;
                }

                // ProgId with shell open command
                using (var progIdKey = classesKey.CreateSubKey(ProgId))
                {
                    progIdKey.SetValue(null, AppName);
                    using var commandKey = progIdKey.CreateSubKey(@"shell\open\command");
                    commandKey.SetValue(null, $"\"{exePath}\" \"%1\"");
                }

                // Register each extension via OpenWithProgids (modern per-user approach —
                // adds to "Open With" list without forcibly overriding current default)
                foreach (var ext in extensions)
                {
                    using var extKey = classesKey.CreateSubKey(ext);
                    using var openWithKey = extKey.CreateSubKey("OpenWithProgids");
                    openWithKey.SetValue(ProgId, string.Empty);
                    logger.LogInformation("Registered {Extension} -> {ProgId}", ext, ProgId);
                }

                // Notify Explorer of the association change
                SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, nint.Zero, nint.Zero);

                logger.LogInformation("File associations registered. Right-click a FITS file -> Open With to select TianWen.");
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register file associations");
                return 1;
            }
        }

        [SupportedOSPlatform("windows")]
        [LibraryImport("shell32.dll")]
        private static partial void SHChangeNotify(int wEventId, int uFlags, nint dwItem1, nint dwItem2);

        private static int RegisterLinux(string exePath, string[] extensions, ILogger logger)
        {
            try
            {
                var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

                // .desktop entry (per-user, NoDisplay=true so it only shows in Open With)
                var appsDir = Path.Combine(dataHome, "applications");
                Directory.CreateDirectory(appsDir);

                var desktopPath = Path.Combine(appsDir, "tianwen-fitsviewer.desktop");
                File.WriteAllText(desktopPath,
                    "[Desktop Entry]\n" +
                    $"Name={AppName}\n" +
                    $"Exec=\"{exePath}\" %f\n" +
                    "Type=Application\n" +
                    "MimeType=application/fits;image/fits;\n" +
                    "NoDisplay=true\n");
                logger.LogInformation("Wrote {DesktopFile}", desktopPath);

                // MIME type definition (per-user under ~/.local/share/mime)
                var mimeDir = Path.Combine(dataHome, "mime", "packages");
                Directory.CreateDirectory(mimeDir);

                var globEntries = string.Join("\n    ", extensions.Select(e => $"<glob pattern=\"*{e}\"/>"));
                var mimeXmlPath = Path.Combine(mimeDir, "tianwen-fitsviewer.xml");
                File.WriteAllText(mimeXmlPath,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                    "<mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\">\n" +
                    "  <mime-type type=\"application/fits\">\n" +
                    "    <comment>FITS Astronomical Image</comment>\n" +
                    $"    {globEntries}\n" +
                    "  </mime-type>\n" +
                    "</mime-info>\n");
                logger.LogInformation("Wrote {MimeXml}", mimeXmlPath);

                // Refresh MIME database and set as default handler
                RunProcess("update-mime-database", Path.Combine(dataHome, "mime"), logger);
                RunProcess("xdg-mime", "default tianwen-fitsviewer.desktop application/fits", logger);

                foreach (var ext in extensions)
                {
                    logger.LogInformation("Registered {Extension} -> application/fits", ext);
                }

                logger.LogInformation("File associations registered via XDG");
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register file associations");
                return 1;
            }
        }

        private static void RunProcess(string fileName, string arguments, ILogger logger)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                });
                process?.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to run {FileName} {Arguments}", fileName, arguments);
            }
        }
    }
}
