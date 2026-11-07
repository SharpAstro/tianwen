using System;
using System.IO;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The one test that goes through the REAL registration (<see cref="TestOutputCleanupAttribute"/> on the assembly), never a
/// <c>Release</c> called by hand: <c>TestOutputCleanupTests</c> pins the registry's rules on instances of their own, and this
/// pins that a test using <see cref="SharedTestData.CreateTempTestOutputDir"/> really has its folder ended by xunit.
/// </summary>
/// <remarks>
/// The assertion is in <c>Dispose</c> because that is the first place a test can look after its own end: xunit runs the
/// attribute's <c>After</c> before it (see that attribute's remarks), and an exception thrown from <c>Dispose</c> fails the test
/// (measured). There is no failing-test twin on purpose: a test that fails by design cannot sit in the suite, and the attribute's
/// remarks record the one-off measurement that a throwing test's folder goes the same way.
/// </remarks>
public sealed class TestOutputEndToEndTests : IDisposable
{
    private string? _dir;

    [Fact]
    public void ATestThatWroteOutputLeavesNothingBehindOnceItEnds()
    {
        _dir = SharedTestData.CreateTempTestOutputDir();
        File.WriteAllText(Path.Combine(_dir, "x.txt"), "x");

        Directory.Exists(_dir).ShouldBeTrue("the folder lives as long as its test runs");
    }

    public void Dispose()
    {
        // TIANWEN_KEEP_TEST_OUTPUT=1 asks for the folders to stay, so there is nothing to assert then.
        if (_dir is not null && !TestOutput.KeepRequested)
        {
            Directory.Exists(_dir).ShouldBeFalse("xunit ended the test, so the registration took its folder");
        }
    }
}
