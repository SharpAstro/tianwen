using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The commit <see cref="BuildInfo"/> reads off an informational version, in each form a build writes it. A CI build
/// stamps "+&lt;sha&gt;" into the version and the SDK's source link appends ".&lt;sha&gt;" to that, so it read the commit
/// twice over (found by the starless plates' provenance record, #2449).
/// </summary>
public class BuildInfoTests
{
    private const string Sha = "3da3ee24f3d1859f56997140b7f9a72a87317e24";

    [Theory]
    [InlineData("10.1.0+" + Sha, "10.1.0", Sha)]
    [InlineData("10.1.1234+" + Sha + "." + Sha, "10.1.1234", Sha)]
    [InlineData("1.0.0-beta+build.3+" + Sha, "1.0.0-beta+build.3", Sha)]
    [InlineData("1.0.0+build.3", "1.0.0", "build.3")]
    [InlineData("6.1.0", "6.1.0", null)]
    [InlineData("6.1.0+", "6.1.0+", null)]
    public void TheCommitIsTheTrailingShaOfTheBuildMetadata(string informational, string version, string? sha)
    {
        var (v, s) = BuildInfo.ParseInformational(informational);
        v.ShouldBe(version);
        s.ShouldBe(sha);
    }
}
