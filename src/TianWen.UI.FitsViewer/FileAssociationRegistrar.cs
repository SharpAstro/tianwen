using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;

namespace TianWen.UI.FitsViewer
{
    internal static partial class FileAssociationRegistrar
    {
        private const string ProgId = "TianWen.FitsViewer";
        private const string AppName = "TianWen FITS Image Viewer";

        /// <summary>
        /// The Explorer thumbnail provider, published beside the executable by CI. The tarball's equivalent
        /// of the MSIX manifest's <c>desktop2:ThumbnailHandler</c> + <c>com:SurrogateServer</c> pair.
        /// </summary>
        private const string ThumbnailDllName = "tianwen-thumb.dll";
        private const string ThumbnailProviderName = "Astro Photo Viewer thumbnail provider";
        private const string PropertyHandlerName = "Astro Photo Viewer property handler";
        private const string PropertyHandlersKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\PropertyHandlers";

        private static readonly Dictionary<string, string[]> ExtensionGroups = new(StringComparer.OrdinalIgnoreCase)
        {
            ["FITS"] = [".fit", ".fits", ".fts", ".fz"],
            ["SER"] = [".ser"]
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

        /// <summary>Removes what <see cref="Register"/> wrote on Windows; other platforms have nothing registered by it here.</summary>
        internal static int Unregister(string group, ILogger logger)
        {
            if (!ExtensionGroups.TryGetValue(group, out var extensions))
            {
                logger.LogError("Unknown extension group: {Group}. Supported: {Supported}", group, string.Join(", ", ExtensionGroups.Keys));
                return 1;
            }

            if (!OperatingSystem.IsWindows())
            {
                logger.LogWarning("--unregister is only implemented on Windows");
                return 1;
            }

            return UnregisterWindows(extensions, logger);
        }

        [SupportedOSPlatform("windows")]
        private static int UnregisterWindows(string[] extensions, ILogger logger)
        {
            try
            {
                using var classesKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
                if (classesKey is null)
                {
                    logger.LogError(@"Cannot open HKCU\Software\Classes for writing");
                    return 1;
                }

                var thumbClsid = ThumbnailRenderer.ShellExtensionClsid.ToString("B");
                var handlerId = ThumbnailRenderer.ThumbnailProviderHandlerId.ToString("B");
                foreach (var ext in extensions)
                {
                    using (var openWith = classesKey.OpenSubKey($@"{ext}\OpenWithProgids", writable: true))
                    {
                        openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
                    }

                    // Only our own entry: another app's thumbnail provider on the extension stays.
                    bool ours;
                    using (var shellEx = classesKey.OpenSubKey($@"{ext}\ShellEx\{handlerId}"))
                    {
                        ours = shellEx is not null && string.Equals(shellEx.GetValue(null) as string, thumbClsid, StringComparison.OrdinalIgnoreCase);
                    }

                    if (ours)
                    {
                        classesKey.DeleteSubKeyTree($@"{ext}\ShellEx\{handlerId}", throwOnMissingSubKey: false);
                    }

                    logger.LogInformation("Unregistered {Extension}", ext);
                }

                classesKey.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
                classesKey.DeleteSubKeyTree(@"CLSID\" + thumbClsid, throwOnMissingSubKey: false);

                UnregisterPropertyHandler(extensions, logger);

                SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, nint.Zero, nint.Zero);
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to unregister file associations");
                return 1;
            }
        }

        /// <summary>Removes the HKLM property handler keys <see cref="RegisterPropertyHandler"/> wrote; one warning when not elevated.</summary>
        [SupportedOSPlatform("windows")]
        private static void UnregisterPropertyHandler(string[] extensions, ILogger logger)
        {
            var clsid = ThumbnailRenderer.PropertyHandlerClsid.ToString("B");
            try
            {
                using (var handlers = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(PropertyHandlersKey, writable: true))
                {
                    foreach (var ext in extensions)
                    {
                        bool ours;
                        using (var extKey = handlers?.OpenSubKey(ext))
                        {
                            ours = extKey is not null && string.Equals(extKey.GetValue(null) as string, clsid, StringComparison.OrdinalIgnoreCase);
                        }

                        if (ours)
                        {
                            handlers?.DeleteSubKeyTree(ext, throwOnMissingSubKey: false);
                        }
                    }
                }

                using var classesKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes", writable: true);
                classesKey?.DeleteSubKeyTree(@"CLSID\" + clsid, throwOnMissingSubKey: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                logger.LogWarning("Explorer detail columns are not unregistered: the HKLM keys need an elevated process ({Reason}).", ex.Message);
            }
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

                // Register each extension via OpenWithProgids (modern per-user approach,
                // adds to "Open With" list without forcibly overriding current default)
                foreach (var ext in extensions)
                {
                    using var extKey = classesKey.CreateSubKey(ext);
                    using var openWithKey = extKey.CreateSubKey("OpenWithProgids");
                    openWithKey.SetValue(ProgId, string.Empty);
                    logger.LogInformation("Registered {Extension} -> {ProgId}", ext, ProgId);
                }

                RegisterThumbnailProvider(classesKey, exePath, extensions, logger);
                RegisterPropertyHandler(exePath, extensions, logger);

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

        /// <summary>
        /// Registers <c>tianwen-thumb.dll</c> as the thumbnail provider for every extension in the group,
        /// so an unpackaged install gets the same Explorer thumbnails the Store package declares in its
        /// manifest. Two keys, both per user: the CLSID's <c>InprocServer32</c> pointing at the DLL, and
        /// each extension's <c>ShellEx\{IThumbnailProvider}</c> naming the CLSID. Under the EXTENSION key,
        /// not our ProgId: the shell consults the extension whichever app the user made the default, and
        /// this app is only ever a candidate. The shell hosts the DLL in its own surrogate process by
        /// default (no <c>DisableProcessIsolation</c> is written), so it never loads into explorer.exe.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static void RegisterThumbnailProvider(Microsoft.Win32.RegistryKey classesKey, string exePath, string[] extensions, ILogger logger)
        {
            var thumbDll = Path.Combine(Path.GetDirectoryName(exePath) ?? string.Empty, ThumbnailDllName);
            if (!File.Exists(thumbDll))
            {
                logger.LogWarning("No {Dll} beside the executable, so Explorer thumbnails are not registered", ThumbnailDllName);
                return;
            }

            var clsid = ThumbnailRenderer.ShellExtensionClsid.ToString("B");
            using (var clsidKey = classesKey.CreateSubKey(@"CLSID\" + clsid))
            {
                clsidKey.SetValue(null, ThumbnailProviderName);
                using var inprocKey = clsidKey.CreateSubKey("InprocServer32");
                inprocKey.SetValue(null, thumbDll);
                inprocKey.SetValue("ThreadingModel", "Both");
            }

            var handlerId = ThumbnailRenderer.ThumbnailProviderHandlerId.ToString("B");
            foreach (var ext in extensions)
            {
                using var shellExKey = classesKey.CreateSubKey($@"{ext}\ShellEx\{handlerId}");
                shellExKey.SetValue(null, clsid);
            }

            logger.LogInformation("Registered Explorer thumbnails for {Extensions} via {Dll}", string.Join(", ", extensions), thumbDll);
        }

        /// <summary>
        /// Registers the Explorer property handler (detail columns, Details pane) in <c>tianwen-thumb.dll</c>.
        /// Windows reads property handlers ONLY from <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\PropertyHandlers\&lt;ext&gt;</c>,
        /// never per user, so this needs an elevated process; without one it logs a single warning and returns.
        /// It never fails the association or thumbnail registration. The Store package declares the same
        /// handler in its manifest and needs none of this. No per-type FullDetails or InfoTip list is written:
        /// the Details pane shows the properties the handler fills, taken from the property definitions.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static void RegisterPropertyHandler(string exePath, string[] extensions, ILogger logger)
        {
            var dll = Path.Combine(Path.GetDirectoryName(exePath) ?? string.Empty, ThumbnailDllName);
            if (!File.Exists(dll))
            {
                logger.LogWarning("No {Dll} beside the executable, so Explorer detail columns are not registered", ThumbnailDllName);
                return;
            }

            var clsid = ThumbnailRenderer.PropertyHandlerClsid.ToString("B");
            try
            {
                using var classesKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes");
                using (var clsidKey = classesKey.CreateSubKey(@"CLSID\" + clsid))
                {
                    clsidKey.SetValue(null, PropertyHandlerName);
                    using var inprocKey = clsidKey.CreateSubKey("InprocServer32");
                    inprocKey.SetValue(null, dll);
                    inprocKey.SetValue("ThreadingModel", "Both");
                }

                foreach (var ext in extensions)
                {
                    using var handlerKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey($@"{PropertyHandlersKey}\{ext}");
                    handlerKey.SetValue(null, clsid);
                }

                logger.LogInformation("Registered Explorer detail columns for {Extensions} via {Dll}", string.Join(", ", extensions), dll);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                logger.LogWarning("Explorer detail columns are not registered: Windows reads property handlers only from HKLM, which needs an elevated process ({Reason}). Run the registration elevated, or install the Store package.", ex.Message);
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
