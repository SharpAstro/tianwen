using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class TestDataSanityTests(ITestOutputHelper testOutputHelper) : ImageAnalyserTests(testOutputHelper)
{
    [Theory]
    [InlineData(PlateSolveTestFile)]
    public async Task GivenOnDiskFitsFileWithImageWhenTryingReadImageItSucceeds(string name)
    {
        // given


        ImageDim dim;
        SharedTestData.TestFileImageDimAndCoords.TryGetValue(name, out var dimAndCoords).ShouldBeTrue();

        (dim, _) = dimAndCoords;

        // when
        Image? image = null;
        await Should.NotThrowAsync(async () => image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: TestContext.Current.CancellationToken));

        // then
        image.ShouldNotBeNull();
        image.Width.ShouldBe(dim.Width);
        image.Height.ShouldBe(dim.Height);
    }
}
