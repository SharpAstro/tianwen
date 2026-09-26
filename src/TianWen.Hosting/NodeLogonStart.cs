using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace TianWen.Hosting;

/// <summary>
/// Starts the machine's node when the user logs on, while the rig is shared (docs/plans/hardware-in-the-server.md,
/// decision 3): a NUC that logs on by itself then has its rig reachable from the laptop after any reboot, with nobody
/// logged on over RDP. What starts is the node's KEEPER, from the server's own directory.
/// </summary>
public interface INodeLogonStart
{
    /// <summary>Whether the entry is there now.</summary>
    bool IsSet { get; }

    /// <summary>Adds the entry, to start <paramref name="serverPath"/>'s keeper, or removes it.</summary>
    void Set(bool startAtLogon, string serverPath);
}

/// <summary>The entry each OS keeps: a <c>Run</c> value on Windows, an XDG autostart entry on Linux, a LaunchAgent on macOS.</summary>
public static class NodeLogonStart
{
    public const string Name = "TianWen node";

    /// <summary>The current user's entry, on this OS.</summary>
    public static INodeLogonStart ForThisUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return new RunKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return new LaunchAgent(Path.Combine(home, "Library", "LaunchAgents"));
        }
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".config");
        return new XdgAutostart(Path.Combine(configHome, "autostart"));
    }

    /// <summary>A value named <see cref="Name"/> under <paramref name="keyPath"/> in the current user's hive.</summary>
    [SupportedOSPlatform("windows")]
    public sealed class RunKey(string keyPath) : INodeLogonStart
    {
        public bool IsSet
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                return key?.GetValue(Name) is not null;
            }
        }

        public void Set(bool startAtLogon, string serverPath)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (startAtLogon)
            {
                key.SetValue(Name, $"\"{serverPath}\" --keeper", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(Name, throwOnMissingValue: false);
            }
        }
    }

    /// <summary>An XDG autostart entry, <c>tianwen-node.desktop</c>, in <paramref name="directory"/>.</summary>
    public sealed class XdgAutostart(string directory) : INodeLogonStart
    {
        public string EntryPath => Path.Combine(directory, "tianwen-node.desktop");

        public bool IsSet => File.Exists(EntryPath);

        public void Set(bool startAtLogon, string serverPath)
        {
            if (!startAtLogon)
            {
                File.Delete(EntryPath);
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(EntryPath, DesktopEntry(serverPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>
        /// The entry: <c>Exec</c> quotes the path, as the desktop entry specification requires of one that may hold a
        /// space, escaping the characters it reserves inside quotes.
        /// </summary>
        internal static string DesktopEntry(string serverPath)
        {
            var quoted = new StringBuilder("\"");
            foreach (var c in serverPath)
            {
                if (c is '"' or '`' or '$' or '\\')
                {
                    quoted.Append('\\');
                }
                quoted.Append(c);
            }
            quoted.Append('"');

            return $"""
                [Desktop Entry]
                Type=Application
                Name={Name}
                Comment=Keeps the TianWen node running, so the rig is reachable on the LAN
                Exec={quoted} --keeper
                Terminal=false
                NoDisplay=true
                X-GNOME-Autostart-enabled=true

                """.Replace("\r\n", "\n");
        }
    }

    /// <summary>A LaunchAgent, <c>org.sharpastro.tianwen.node.plist</c>, in <paramref name="directory"/>, loaded at the next logon.</summary>
    public sealed class LaunchAgent(string directory) : INodeLogonStart
    {
        public const string Label = "org.sharpastro.tianwen.node";

        public string PlistPath => Path.Combine(directory, Label + ".plist");

        public bool IsSet => File.Exists(PlistPath);

        public void Set(bool startAtLogon, string serverPath)
        {
            if (!startAtLogon)
            {
                File.Delete(PlistPath);
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(PlistPath, Plist(serverPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        internal static string Plist(string serverPath) => $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{Label}</string>
              <key>ProgramArguments</key>
              <array>
                <string>{System.Security.SecurityElement.Escape(serverPath)}</string>
                <string>--keeper</string>
              </array>
              <key>RunAtLoad</key>
              <true/>
            </dict>
            </plist>

            """.Replace("\r\n", "\n");
    }
}
