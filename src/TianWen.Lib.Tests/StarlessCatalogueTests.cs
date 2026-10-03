using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class StarlessCatalogueTests
{
    [Fact]
    public async Task ACatalogueReadsBackWhatWasWrittenAndAnOlderOneReadsNoSky()
    {
        var dir = Directory.CreateTempSubdirectory("tianwen-catalogue-").FullName;
        try
        {
            var path = StarlessCatalogue.PathFor(dir, "m42");
            var stars = ImmutableArray.Create(
                new FittedStar(10.25f, 20.5f, 42f, 0.125f, 1.02f, 0.1f, 0.001f, StarFitOutcome.Subtracted, false, false, 0.9f, -0.2f, false, -0.5f,
                    StarFitModel.Moffat, [0.1f, 0.12f, 0.08f], 812.4f, 1.604f),
                new FittedStar(30f, 40f, 6f, 0.01f, 2.9f, 0.1f, 0.001f, StarFitOutcome.Knot, false, false, float.NaN, float.NaN, false, float.NaN));
            await StarlessCatalogue.WriteAsync(path, stars, TestContext.Current.CancellationToken);

            var read = await StarlessCatalogue.ReadAsync(path, TestContext.Current.CancellationToken);
            read.Length.ShouldBe(2);
            read[0].SkyAbove.ShouldBe(812.4f, 0.05f);
            read[0].Texture.ShouldBe(1.604f, 0.0005f);
            read[0].ChannelAmplitudes.Length.ShouldBe(3);
            float.IsNaN(read[1].SkyAbove).ShouldBeTrue();
            float.IsNaN(read[1].Texture).ShouldBeTrue();

            // A file from before the sky columns: sixteen fields.
            await File.WriteAllTextAsync(path,
                "x,y,significance,amplitude,width,sky,sigma,outcome,saturated,inpainted,core_residual,core_bias,second_pass,hole_depth,model,amplitudes\n" +
                "10.25,20.50,42.0,0.125,1.020,0.1,0.001,Subtracted,0,0,0.900,-0.200,0,-0.50,Moffat,0.1;0.12;0.08\n",
                TestContext.Current.CancellationToken);
            var old = await StarlessCatalogue.ReadAsync(path, TestContext.Current.CancellationToken);
            old[0].Model.ShouldBe(StarFitModel.Moffat);
            float.IsNaN(old[0].SkyAbove).ShouldBeTrue();
            float.IsNaN(old[0].Texture).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
