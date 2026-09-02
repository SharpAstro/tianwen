using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the properties of <see cref="FileEnumeration"/> that differ from the legacy
    /// <c>Directory.EnumerateFiles(path, pattern, SearchOption)</c> overloads every scan used to call:
    /// suffix matching that is case-insensitive on every OS and treats <c>.fits.gz</c> as one extension,
    /// hidden files INCLUDED, recursion under the caller's control, a missing root still throwing, and
    /// reparse points neither listed nor entered (the organized archive's junction farm used to be scanned
    /// once per link, and a scratch junction into the archive turned a scratch walk into an archive walk).
    /// </summary>
    public sealed class FileEnumerationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "fileenum-" + Guid.NewGuid().ToString("N")[..8]);

        public FileEnumerationTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private string Touch(string relative)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0x53]);
            return path;
        }

        [Fact]
        public void GivenMixedCaseAndCompoundExtensionsWhenEnumeratingThenTheSuffixMatchIsCaseInsensitive()
        {
            var a = Touch("a.fits");
            var b = Touch("B.FITS");
            var c = Touch("c.fits.gz");
            Touch("d.txt");
            Touch("e.fitsx");

            var found = FileEnumeration.EnumerateFiles(_root, [".fits", ".fits.gz"], recursive: false)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            found.ShouldBe(new[] { a, b, c }.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        [Fact]
        public void GivenNestedFilesWhenEnumeratingThenRecursionIsTheCallersChoice()
        {
            var top = Touch("top.fits");
            var deep = Touch(Path.Combine("sub", "deeper", "deep.fits"));

            FileEnumeration.EnumerateFiles(_root, ".fits", recursive: false).ShouldBe([top]);
            FileEnumeration.EnumerateFiles(_root, ".fits", recursive: true)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ShouldBe(new[] { deep, top }.Order(StringComparer.OrdinalIgnoreCase));
            FileEnumeration.CountFiles(_root, ".fits", recursive: true).ShouldBe(2);
            FileEnumeration.CountFiles(_root, ".fits", recursive: false).ShouldBe(1);
        }

        [Fact]
        public void GivenAHiddenFileWhenEnumeratingThenItIsIncluded()
        {
            // The new BCL API skips Hidden | System by default; the legacy overloads never did, and a
            // capture tool may mark a folder however it likes. On non-Windows the attribute is a no-op and
            // the file is plain, so the assertion still holds.
            var hidden = Touch("hidden.fits");
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

            FileEnumeration.EnumerateFiles(_root, ".fits", recursive: true).ShouldBe([hidden]);
        }

        [Fact]
        public void GivenAMissingRootWhenEnumeratingThenItThrowsLikeTheLegacyOverloadDid()
        {
            var missing = Path.Combine(_root, "nope");
            Should.Throw<DirectoryNotFoundException>(() => FileEnumeration.EnumerateFiles(missing, ".fits", recursive: true).ToList());
        }

        [Fact]
        public void GivenADirectoryLinkInsideTheTreeWhenEnumeratingThenTheLinkIsNeitherListedNorEntered()
        {
            var real = Touch(Path.Combine("real", "linked.fits"));
            var link = Path.Combine(_root, "link");
            if (!TryCreateDirectoryLink(link, Path.Combine(_root, "real")))
            {
                Assert.Skip("Creating a directory link needs a privilege this host does not grant (Developer Mode or mklink /J).");
            }

            var found = FileEnumeration.EnumerateFiles(_root, ".fits", recursive: true).ToArray();

            found.ShouldBe([real]);
            found.ShouldAllBe(p => !p.Contains(Path.DirectorySeparatorChar + "link" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }

        /// <summary>A symbolic link where the host allows one; on Windows a junction via <c>mklink /J</c>
        /// otherwise, which needs no privilege. False when neither could be made.</summary>
        private static bool TryCreateDirectoryLink(string link, string target)
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
                return Directory.Exists(link);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return false;
                }
            }

            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            mklink?.WaitForExit();
            return Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
    }
}
