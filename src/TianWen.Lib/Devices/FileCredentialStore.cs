using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TianWen.Lib.Devices;

/// <summary>
/// <see cref="ICredentialStore"/> fallback for non-Windows platforms: one file per secret under
/// <c>{AppData}/Secrets</c>, restricted to the owner (<c>0600</c>) where the OS supports it. This is
/// the same protection level the profile JSON already has; a libsecret / macOS-Keychain backend can
/// drop in later behind the same interface. Windows uses <see cref="WindowsCredentialStore"/> instead.
/// </summary>
internal sealed class FileCredentialStore(IExternal external) : ICredentialStore
{
    private readonly Lock _gate = new(); // serializes Get/Set against the per-key secret file; Set's write-to-temp-then-rename must not interleave with a concurrent read of the same key

    public string? Get(string key)
    {
        lock (_gate)
        {
            var file = PathFor(key);
            if (!File.Exists(file))
            {
                return null;
            }

            var value = File.ReadAllText(file, Encoding.UTF8);
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            var file = PathFor(key);

            if (value is null)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
                return;
            }

            // Write to a temp file (restricted before it carries the secret) then rename, so a
            // reader never sees a half-written or world-readable secret file. The temp name is
            // this write's own: the lock above covers this process only, and a GUI and a server
            // can both store a secret (a fixed name had the second writer fail on the first's).
            var tmp = $"{file}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    RestrictToOwner(tmp);
                    var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(value);
                    stream.Write(bytes, 0, bytes.Length);
                }
                File.Move(tmp, file, overwrite: true);
            }
            finally
            {
                File.Delete(tmp);
            }
            RestrictToOwner(file);
        }
    }

    private string PathFor(string key)
    {
        var dir = external.CreateSubDirectoryInAppDataFolder("Secrets");
        return Path.Combine(dir.FullName, external.GetSafeFileName(key) + ".cred");
    }

    private static void RestrictToOwner(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
