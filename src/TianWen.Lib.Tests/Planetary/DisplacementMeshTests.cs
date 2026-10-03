using System;
using System.Linq;
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
    public void AResidualGainScalesTheLocalFieldAndOneLeavesItAsRead()
    {
        // #1081: a point's reading is shrunk, so the mesh can scale each residual's departure from the points' mean before it blends
        // them, the mean (the points' correction to the global shift) kept as read. One must build the very mesh the residuals as
        // read build, and with residuals whose mean is zero, two must double every node's offset from the global shift.
        AlignmentPointShift[] aps = [new AlignmentPointShift(30, 40, 0.6f, -0.3f), new AlignmentPointShift(52, 46, -0.2f, 0.45f), new AlignmentPointShift(44, 70, -0.4f, -0.15f)];
        var asRead = DisplacementMesh.Build(100, 100, 0.5f, -0.25f, aps, nodeSpacing: 4, influence: 6);
        var one = DisplacementMesh.Build(100, 100, 0.5f, -0.25f, aps, nodeSpacing: 4, influence: 6, residualGain: 1f);
        var two = DisplacementMesh.Build(100, 100, 0.5f, -0.25f, aps, nodeSpacing: 4, influence: 6, residualGain: 2f);

        for (var y = 0f; y < 100; y += 3.5f)
        {
            for (var x = 0f; x < 100; x += 3.5f)
            {
                one.Sample(x, y).ShouldBe(asRead.Sample(x, y));
                var (ax, ay) = asRead.Sample(x, y);
                var (tx, ty) = two.Sample(x, y);
                (tx - 0.5f).ShouldBe(2 * (ax - 0.5f), 1e-5);
                (ty + 0.25f).ShouldBe(2 * (ay + 0.25f), 1e-5);
            }
        }

        // A common shift of every residual is the global correction, and the gain leaves it as read.
        AlignmentPointShift[] common = [.. aps.Select(a => a with { ResidualX = a.ResidualX + 0.3f })];
        var commonOne = DisplacementMesh.Build(100, 100, 0.5f, -0.25f, common, nodeSpacing: 4, influence: 6);
        var commonTwo = DisplacementMesh.Build(100, 100, 0.5f, -0.25f, common, nodeSpacing: 4, influence: 6, residualGain: 2f);
        var (cx1, _) = commonOne.Sample(44, 52);
        var (cx2, _) = commonTwo.Sample(44, 52);
        var (x2, _) = two.Sample(44, 52);
        var (x1, _) = asRead.Sample(44, 52);
        (cx2 - cx1).ShouldBe(x2 - x1, 1e-5, "the common 0.3 px moves both meshes alike, never doubled");
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
