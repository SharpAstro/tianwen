using Shouldly;
using TianWen.Cli;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The code <c>tianwen solve</c> ends with, for a script that branches on it (#2264): 2 says the frame was not solved
/// and must not be hidden by an export that failed beside it, which is 3 on a solved run only.
/// </summary>
public class SolveExitCodeTests
{
    [Theory]
    [InlineData(true, false, 0)]    // solved, every export written
    [InlineData(true, true, 3)]     // solved, an export failed
    [InlineData(false, false, 2)]   // no solution
    [InlineData(false, true, 2)]    // no solution, and the star export failed as well: still no solution
    public void NoSolutionKeepsItsCodeWhateverAnExportDid(bool solved, bool anExportFailed, int expected)
    {
        SolveSubCommand.ExitCode(solved, anExportFailed).ShouldBe(expected);
    }
}
