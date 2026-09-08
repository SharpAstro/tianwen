using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The prepared bug report (P26): what the link carries, and more importantly what it must never
    /// carry.
    /// </summary>
    /// <remarks>
    /// The note this came from asked for logs to be attached automatically. They are not, and these
    /// tests are where that decision is enforced rather than merely written down: a log lists every
    /// folder opened, folder names carry target and site names, and a run of them says where someone
    /// was and when they were not at home. The link says where the logs are and asks; the user
    /// attaches.
    /// </remarks>
    public class BugReportLinkTests
    {
        private const string Build = "7.0.1568 (Release, win-arm64)";
        private const string Os = "Microsoft Windows 10.0.26200";

        private static string BodyOf(string url)
        {
            var query = new Uri(url).Query;
            var body = query.Split('&').Single(p => p.StartsWith("body=", StringComparison.Ordinal))["body=".Length..];
            return Uri.UnescapeDataString(body);
        }

        [Fact]
        public void TheLinkIsAGitHubIssueDraftForThisRepository()
        {
            var url = BugReportLink.ForViewer(Build, Os, ["backend: none"]);

            var uri = new Uri(url, UriKind.Absolute);
            uri.Scheme.ShouldBe("https");
            uri.Host.ShouldBe("github.com");
            uri.AbsolutePath.ShouldBe("/SharpAstro/tianwen/issues/new");
            uri.Query.ShouldContain("title=");
            uri.Query.ShouldContain("body=");
        }

        /// <summary>The half a user cannot produce accurately, which is the reason to prefill anything.</summary>
        [Fact]
        public void TheBodyCarriesTheBuildTheOsAndTheAiBlock()
        {
            var body = BodyOf(BugReportLink.ForViewer(Build, Os, ["backend: SETI Astro", "models: 3 resolved"]));

            body.ShouldContain(Build);
            body.ShouldContain(Os);
            body.ShouldContain("backend: SETI Astro");
            body.ShouldContain("models: 3 resolved");
        }

        /// <summary>
        /// The assertion the feature exists to satisfy: no log content, and no path that names the
        /// person or where they have been.
        /// </summary>
        /// <remarks>
        /// An absolute Windows path is the specific leak, because it starts with the account name, and
        /// the environment-variable form carries the same information to the reader without it. A
        /// document path would be worse still: it is the folder the user is working in, which is the
        /// target they are shooting.
        /// </remarks>
        [Fact]
        public void TheBodyNamesNoAbsolutePathAndAttachesNoLog()
        {
            var body = BodyOf(BugReportLink.ForViewer(Build, Os, ["backend: none"]));

            // The reader still has to be able to find the log, and to know it was withheld on purpose.
            body.ShouldContain("%LOCALAPPDATA%");
            body.ShouldContain("Not attached");
            body.ShouldNotContain(@"C:\Users");
            body.ShouldNotContain("/Users/");
            body.ShouldNotContain(@"C:\");
        }

        /// <summary>
        /// A pathological AI block must not produce a link a browser silently truncates. The block is
        /// the only part that grows with the machine, so it is the part that goes.
        /// </summary>
        [Fact]
        public void AnOversizedAiBlockIsDroppedRatherThanOverflowingTheLink()
        {
            var many = Enumerable.Range(0, 200)
                .Select(i => $"probed directory {i}: " + new string('d', 200))
                .ToArray();

            var url = BugReportLink.ForViewer(Build, Os, many);

            url.Length.ShouldBeLessThanOrEqualTo(BugReportLink.MaxUrlLength);
            var body = BodyOf(url);
            // The environment that FITS survives the trim, and the absence is stated rather than silent.
            body.ShouldContain(Build);
            body.ShouldContain(Os);
            body.ShouldContain("omitted");
        }

        /// <summary>
        /// Every input here comes from somewhere else (a version string, an OS description, a probe
        /// result), and a newline in any of them would break the markdown list it lands in.
        /// </summary>
        [Theory]
        [InlineData("7.0\nInjected: heading")]
        [InlineData("7.0\r\nInjected: heading")]
        public void ANewlineInAnInputCannotBreakTheList(string build)
        {
            var body = BodyOf(BugReportLink.ForViewer(build, Os, ["backend: none"]));

            var buildLine = body.Split('\n').Single(l => l.StartsWith("- Build:", StringComparison.Ordinal));
            // Flattened onto the same line rather than dropped.
            buildLine.ShouldContain("Injected: heading");
        }

        [Fact]
        public void NoAiProbeIsSaidRatherThanLeftBlank()
        {
            BodyOf(BugReportLink.ForViewer(Build, Os, null)).ShouldContain("not probed");
            BodyOf(BugReportLink.ForViewer(Build, Os, [])).ShouldContain("not probed");
        }

        [Fact]
        public void AMissingBuildOrOsReadsAsUnknownRatherThanAsAnEmptyBullet()
        {
            var body = BodyOf(BugReportLink.ForViewer("", "  ", ["backend: none"]));

            body.ShouldContain("- Build: unknown");
            body.ShouldContain("- OS: unknown");
        }
    }
}
