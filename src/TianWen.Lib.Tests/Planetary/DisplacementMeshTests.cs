using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class DisplacementMeshTests
{
    [Fact]
    public void Sample_at_alignment_point_returns_global_plus_residual()
    {
        AlignmentPointShift[] aps = [new AlignmentPointShift(50, 50, 2f, -1f)];
        var mesh = DisplacementMesh.Build(100, 100, 0.5f, 0.5f, aps, nodeSpacing: 10, influence: 8, regularization: 0.05f);

        var (ox, oy) = mesh.Sample(50, 50);
        ox.ShouldBe(0.5f + 2f, 0.4); // baseline + residual near the AP
        oy.ShouldBe(0.5f - 1f, 0.4);
    }

    [Fact]
    public void Sample_far_from_points_returns_global_baseline()
    {
        AlignmentPointShift[] aps = [new AlignmentPointShift(80, 80, 5f, 5f)];
        var mesh = DisplacementMesh.Build(100, 100, 0.5f, -0.5f, aps, nodeSpacing: 10, influence: 8, regularization: 0.25f);

        var (ox, oy) = mesh.Sample(5, 5);
        ox.ShouldBe(0.5f, 0.2);
        oy.ShouldBe(-0.5f, 0.2);
    }

    [Fact]
    public void Empty_points_give_constant_global_field()
    {
        var mesh = DisplacementMesh.Build(64, 64, 3f, -2f, ReadOnlySpan<AlignmentPointShift>.Empty);

        var (ox, oy) = mesh.Sample(31, 17);
        ox.ShouldBe(3f, 1e-4);
        oy.ShouldBe(-2f, 1e-4);
    }

    [Fact]
    public async Task WarpByMesh_with_constant_field_is_a_pure_translation()
    {
        // Textured 32x32 so sampling is unambiguous.
        var px = new float[32, 32];
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                px[y, x] = (((x * 131) + (y * 977) + 7) % 1000) / 1000f;
            }
        }

        var img = Image.FromChannel(px);
        var mesh = DisplacementMesh.Build(32, 32, 3f, -2f, ReadOnlySpan<AlignmentPointShift>.Empty);

        var warped = await img.WarpByMeshAsync(mesh, TestContext.Current.CancellationToken);

        // Constant offset (3, -2): warped(x, y) = img(x + 3, y - 2). Integer offset -> exact.
        warped[0, 10, 10].ShouldBe(img[0, 8, 13], 1e-5f);
        warped[0, 20, 5].ShouldBe(img[0, 18, 8], 1e-5f);
    }
}
