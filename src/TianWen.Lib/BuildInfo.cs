using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;

namespace TianWen.Lib
{
    /// <summary>
    /// What commit and build produced the running binary, so a process can say so out loud.
    ///
    /// <para><b>Why this exists.</b> A dataset bake was launched with <c>--no-build</c> against a
    /// binary built three hours and two commits earlier. It ran 100 minutes and 16 of 68 sessions,
    /// and nothing about it looked wrong: the archive scan was correct, the tiles were structurally
    /// complete, the manifest matched the files on disk exactly, and not one warning was emitted.
    /// It was caught only by comparing the produced masters' pixel statistics against the previous
    /// bake and finding them identical to six decimal places. A binary that announces its own
    /// provenance turns that into a glance at the first line of the log.</para>
    ///
    /// <para><b>The SHA costs nothing, which is the point.</b> The .NET SDK's built-in source-link
    /// support already appends the commit to <see cref="AssemblyInformationalVersionAttribute"/>
    /// (<c>6.1.0+91cf229342b3...</c>) on every build of a git working tree. Nothing read it. So
    /// there is no build-time stamping here, no <c>Exec</c> git call, and no effect on
    /// <c>Deterministic</c>: this only parses what is already in the assembly.</para>
    ///
    /// <para><b>It reports THIS assembly, not the entry assembly, and that is the load-bearing
    /// choice.</b> An incremental build that recompiles only a dependency leaves the entry
    /// assembly's file untouched: measured here, <c>TianWen.Lib.dll</c> was rewritten at 17:36:57
    /// while <c>tianwen.exe</c> still read 17:35:51 from the previous build. Reporting the entry
    /// assembly would therefore have shown a stale timestamp after a perfectly correct rebuild, and
    /// would have attributed the wrong provenance to the code that actually does the work, which is
    /// all in this library. So the numbers below describe <see cref="BuildInfo"/>'s own assembly,
    /// and <see cref="Describe"/> flags a MIXED build when the entry assembly disagrees.</para>
    ///
    /// <para>A mixed build is worth shouting about in this repo specifically: the
    /// <c>UseLocalSiblings</c> switch silently swaps sibling source for a NuGet package when a
    /// checkout is missing, so a host can end up bound to a released library while the working tree
    /// says otherwise, with no version bump anywhere to show it.</para>
    ///
    /// <para><b>A SHA alone does not catch a stale binary</b>, because a stale binary carries a
    /// perfectly valid SHA. What catches it is comparing that SHA against the working tree's HEAD,
    /// which is why the value has to be VISIBLE rather than merely recorded, and why
    /// <c>tools/run-dataset-bake.ps1</c> refuses to launch when they differ. The two are
    /// complements: the script blocks the mistake, this attributes the artifact after the fact.</para>
    /// </summary>
    public static class BuildInfo
    {
        private const int ShortShaLength = 8;

        private static readonly Lazy<Stamp> _self = new Lazy<Stamp>(() => For(typeof(BuildInfo).Assembly));
        private static readonly Lazy<Stamp?> _entry =
            new Lazy<Stamp?>(() => Assembly.GetEntryAssembly() is { } e ? For(e) : null);

        /// <summary>Informational version of this library with any <c>+sha</c> suffix removed, e.g. <c>6.1.0</c>.</summary>
        public static string Version => _self.Value.Version;

        /// <summary>Full commit SHA this library was built from, or <see langword="null"/> when the
        /// build carried no source-control information (a source drop, or a tree that is not a git
        /// checkout).</summary>
        public static string? CommitSha => _self.Value.Sha;

        /// <summary>First <see cref="ShortShaLength"/> characters of <see cref="CommitSha"/>, for
        /// eyeballing against <c>git log</c>, or <see langword="null"/> when unknown.</summary>
        public static string? ShortCommitSha => Shorten(_self.Value.Sha);

        /// <summary>
        /// When this library was produced, taken from the file's last-write time at runtime rather
        /// than stamped in at build time. Deliberate: embedding a timestamp would make two builds of
        /// identical source differ, which costs reproducibility for a value the filesystem already
        /// holds. It is also the exact number a stale-binary check compares.
        /// <see langword="null"/> when the path cannot be resolved (single-file, where the library
        /// has no file of its own).
        /// </summary>
        public static DateTime? BuiltUtc => _self.Value.BuiltUtc;

        /// <summary>
        /// Directory the running binary was launched from -- the single most diagnostic field in a
        /// log someone else sent you, because on Windows it names the INSTALL KIND without anything
        /// having to detect it.
        /// <para>
        /// A Store / MSIX install answers
        /// <c>C:\Program Files\WindowsApps\SharpAstro.AstroPhotoViewer_6.3.1352.0_x64__jgmekrdtdb020\</c>
        /// -- which carries the package name, its version, the architecture and the package family
        /// in one string, so packaged-ness is derivable from the path and needs no
        /// <c>GetCurrentPackageFullName</c> P/Invoke. A dev run answers a <c>bin\Debug\net10.0</c>
        /// path, an AOT publish answers wherever it was unpacked to. It is also where
        /// <c>ModelResolver</c> probes for app-local weights first, so "which models can this
        /// install even see" starts here.
        /// </para>
        /// <para>
        /// <see cref="AppContext.BaseDirectory"/> rather than <c>Assembly.Location</c>: the latter
        /// is empty under single-file publish, which is exactly the shape the AOT binaries ship in.
        /// </para>
        /// </summary>
        public static string InstallFolder => AppContext.BaseDirectory;

        /// <summary>
        /// One line, safe to print before any logging is configured. Shows local time because it is
        /// read by a human comparing it against when they last built, not by a machine.
        /// <para>
        /// Deliberately WITHOUT <see cref="InstallFolder"/>: this is the CLI's console greeting and
        /// a 90-character path would swamp it. The log banner
        /// (<c>TianWen.Lib.Logging.FileLoggerProvider</c>) pairs the two, because a log is read
        /// after the fact by someone who does not know which install produced it.
        /// </para>
        /// </summary>
        public static string Describe()
        {
            var self = _self.Value;
            var built = self.BuiltUtc is { } utc
                ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "build time unknown";
            var line = $"{self.Version}+{Shorten(self.Sha) ?? "no-scm-info"} built {built}";

            // Only a DISAGREEMENT is worth the reader's attention; matching SHAs are the normal case
            // and naming both every time would train the eye to skip the line.
            if (_entry.Value is { } entry && entry.Sha is { Length: > 0 } && self.Sha is { Length: > 0 }
                && !string.Equals(entry.Sha, self.Sha, StringComparison.OrdinalIgnoreCase))
            {
                line += $" [MIXED BUILD: {entry.Name} from {Shorten(entry.Sha)}]";
            }

            return line;
        }

        private static string? Shorten(string? sha) =>
            sha is { Length: >= ShortShaLength } s ? s[..ShortShaLength] : sha;

        private static Stamp For(Assembly assembly)
        {
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            string version;
            string? sha = null;
            if (informational is { Length: > 0 })
            {
                // Source link appends "+<40 hex>". Split on the LAST '+' so a prerelease label that
                // already contains one (1.0.0-beta+build.3+sha) still yields the trailing SHA.
                var plus = informational.LastIndexOf('+');
                if (plus > 0 && plus < informational.Length - 1)
                {
                    version = informational[..plus];
                    sha = informational[(plus + 1)..];
                }
                else
                {
                    version = informational;
                }
            }
            else
            {
                version = assembly.GetName().Version?.ToString() ?? "unknown";
            }

            return new Stamp(assembly.GetName().Name ?? "unknown", version, sha, BuiltUtcOf(assembly));
        }

        // IL3000 warns that Location is always empty under single-file, which is precisely the case
        // handled here: empty falls through to Environment.ProcessPath. Using ProcessPath alone
        // would be WORSE, not simpler, because `dotnet path/to/tianwen.dll` makes it the shared
        // host and the answer silently becomes the SDK's install date.
        //
        // The suppression has to be the ATTRIBUTE, not the `#pragma warning disable` that was here
        // first. IL3000 is raised twice by two different tools: the Roslyn AOT analyzer at compile
        // time, which a pragma does silence, and ILC/illink at PUBLISH time over the IL, which it
        // does not -- and only the publish leg builds native. So a plain `dotnet build` looked
        // clean while every `dotnet publish -r <rid>` still reported it, which is a bad place for a
        // warning to live: it appears only in the release path, and only on the six-leg AOT matrix.
        [UnconditionalSuppressMessage("SingleFile", "IL3000:Avoid accessing Assembly file path when publishing as a single file",
            Justification = "The empty-string return under single-file is the handled case: it falls through to Environment.ProcessPath, which is the correct answer for a single-file app. Reading Location first is what keeps `dotnet <app>.dll` from reporting the shared host's timestamp.")]
        private static DateTime? BuiltUtcOf(Assembly assembly)
        {
            var location = assembly.Location;
            var path = location is { Length: > 0 } ? location : Environment.ProcessPath;

            try
            {
                return path is { Length: > 0 } && File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private sealed record Stamp(string Name, string Version, string? Sha, DateTime? BuiltUtc);
    }
}
