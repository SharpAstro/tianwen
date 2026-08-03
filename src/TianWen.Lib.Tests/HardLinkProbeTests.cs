using System;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The probe is the whole basis of the re-link verification in <see cref="FitsHeaderEditor"/>,
    /// which decides whether it is safe to move a name in an irreplaceable archive. So its answers
    /// are pinned directly rather than only through the editor: "same file" and "how many names" are
    /// the two facts everything above them trusts.
    /// </summary>
    [Collection("Imaging")]
    public class HardLinkProbeTests
    {
        private static string CreateTempDir([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "TianWen.HardLinkProbeTests", name ?? "unnamed", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteFile(string dir, string name, string content = "frame")
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static void LinkOrSkip(string link, string existing)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard links are only handled on Windows.");
            Assert.SkipUnless(
                HardLinkProbe.TryCreateHardLink(link, existing, out var error),
                $"Could not create a hard link on this volume: {error}");
        }

        private static HardLinkProbe.FileIdentity IdentityOf(string path)
        {
            var identity = HardLinkProbe.TryGetIdentity(path);
            identity.ShouldNotBeNull();
            return identity.Value;
        }

        [Fact]
        public void GivenAnOrdinaryFile_WhenProbed_ThenItReportsExactlyOneName()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard links are only handled on Windows.");
            var path = WriteFile(CreateTempDir(), "solo.bin");

            IdentityOf(path).LinkCount.ShouldBe(1);
        }

        [Fact]
        public void GivenTwoNamesForOneFile_WhenProbed_ThenBothReportTheSameFileAndTwoNames()
        {
            var dir = CreateTempDir();
            var path = WriteFile(dir, "a.bin");
            var link = Path.Combine(dir, "b.bin");
            LinkOrSkip(link, path);

            var first = IdentityOf(path);
            var second = IdentityOf(link);

            first.IsSameFileAs(second).ShouldBeTrue();
            first.LinkCount.ShouldBe(2);
            second.LinkCount.ShouldBe(2);
            // Equality is the whole record, so it also covers the count agreeing.
            first.ShouldBe(second);
        }

        [Fact]
        public void GivenTwoSeparateFilesWithIdenticalContent_WhenProbed_ThenTheyAreNotTheSameFile()
        {
            // The distinction the verification rests on: same bytes is not same file. A copy must
            // never be mistaken for a link, or re-linking would silently discard the copy.
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard links are only handled on Windows.");
            var dir = CreateTempDir();
            var a = WriteFile(dir, "a.bin", "identical");
            var b = WriteFile(dir, "b.bin", "identical");

            IdentityOf(a).IsSameFileAs(IdentityOf(b)).ShouldBeFalse();
            IdentityOf(a).LinkCount.ShouldBe(1);
        }

        [Fact]
        public void GivenSeveralNames_WhenEnumerated_ThenEveryOneComesBackAsAFullPath()
        {
            var dir = CreateTempDir();
            var path = WriteFile(dir, "a.bin");
            var second = Path.Combine(dir, "b.bin");
            var third = Path.Combine(dir, "sub", "c.bin");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            LinkOrSkip(second, path);
            LinkOrSkip(third, path);

            var links = HardLinkProbe.EnumerateLinks(path);

            // Volume-relative is what the API returns, so a rooted path is the contract being pinned:
            // an unrooted one is unusable by every caller and would break re-linking silently.
            links.Length.ShouldBe(3);
            links.ShouldAllBe(p => Path.IsPathFullyQualified(p));
            links.ShouldBe([path, second, third], ignoreOrder: true);
        }

        [Fact]
        public void GivenASingleName_WhenEnumerated_ThenTheFileItselfIsTheOnlyAnswer()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard links are only handled on Windows.");
            var path = WriteFile(CreateTempDir(), "solo.bin");

            HardLinkProbe.EnumerateLinks(path).ShouldHaveSingleItem().ShouldBe(path);
        }

        [Fact]
        public void GivenALinkThatIsThenRemoved_WhenProbed_ThenTheCountDropsButTheFileIsUnchanged()
        {
            // Exactly the transition the editor verifies after a replace: one name goes away, the
            // remaining names still hold the same file.
            var dir = CreateTempDir();
            var path = WriteFile(dir, "a.bin");
            var link = Path.Combine(dir, "b.bin");
            LinkOrSkip(link, path);
            var shared = IdentityOf(path);

            File.Delete(link);

            var after = IdentityOf(path);
            after.IsSameFileAs(shared).ShouldBeTrue();
            after.LinkCount.ShouldBe(1);
            HardLinkProbe.EnumerateLinks(path).ShouldHaveSingleItem().ShouldBe(path);
        }

        [Fact]
        public void GivenAMissingFile_WhenProbed_ThenItReportsUnknownRatherThanThrowing()
        {
            // A probe is asked about files that may be locked or gone, and it is called from inside a
            // sweep over thousands of them. Throwing would abandon the sweep.
            var path = Path.Combine(CreateTempDir(), "never-written.bin");

            HardLinkProbe.TryGetIdentity(path).ShouldBeNull();
            HardLinkProbe.EnumerateLinks(path).ShouldBeEmpty();
        }

        [Fact]
        public void GivenAnExistingTarget_WhenCreatingALinkOverIt_ThenItFailsWithAReadableReason()
        {
            // The re-link path relies on this failing rather than clobbering, which is why it stages
            // the new link under a scratch name and renames it over the sibling instead.
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Hard links are only handled on Windows.");
            var dir = CreateTempDir();
            var path = WriteFile(dir, "a.bin");
            var occupied = WriteFile(dir, "b.bin", "something else");

            HardLinkProbe.TryCreateHardLink(occupied, path, out var error).ShouldBeFalse();
            error.ShouldNotBeEmpty();
            File.ReadAllText(occupied).ShouldBe("something else");
        }
    }
}
