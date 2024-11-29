using Xunit.Abstractions;

namespace TianWen.Lib.Tests;

public abstract class ImageAnalyserTests(ITestOutputHelper testOutputHelper)
{
    internal const string PlateSolveTestFile = nameof(PlateSolveTestFile);
    internal const string PHD2SimGuider = nameof(PHD2SimGuider);

    protected readonly ITestOutputHelper _testOutputHelper = testOutputHelper;

}
