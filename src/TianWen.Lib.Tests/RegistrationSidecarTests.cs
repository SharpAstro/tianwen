using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class RegistrationSidecarTests
{
    private static string CreateTempDir([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TianWen.RegSidecarTests", name ?? "unnamed", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static RegistrationResult SampleResult(string path, float dx = 5f, float dy = -3f) =>
        RegistrationResult.FromTransform(
            path,
            new Matrix3x2(0.9998f, 0.005f, -0.005f, 0.9998f, dx, dy),
            starsMatched: 42);

    [Fact]
    public void PathFor_AppendsSuffix()
    {
        RegistrationSidecar.PathFor(@"C:\data\light_01.fits").ShouldBe(@"C:\data\light_01.fits.match.json");
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsAllFields()
    {
        var dir = CreateTempDir();
        var lightPath = Path.Combine(dir, "light_01.fits");
        // Sidecar references the light by path, but the light file doesn't
        // need to exist on disk for the sidecar write itself.
        var original = SampleResult(lightPath);

        await RegistrationSidecar.WriteAsync(original, TestContext.Current.CancellationToken);
        var loaded = await RegistrationSidecar.TryReadAsync(lightPath, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.LightPath.ShouldBe(original.LightPath);
        loaded.M11.ShouldBe(original.M11, tolerance: 1e-6f);
        loaded.M12.ShouldBe(original.M12, tolerance: 1e-6f);
        loaded.M21.ShouldBe(original.M21, tolerance: 1e-6f);
        loaded.M22.ShouldBe(original.M22, tolerance: 1e-6f);
        loaded.OffsetX.ShouldBe(original.OffsetX, tolerance: 1e-6f);
        loaded.OffsetY.ShouldBe(original.OffsetY, tolerance: 1e-6f);
        loaded.StarsMatched.ShouldBe(original.StarsMatched);
        loaded.Registered.ShouldBe(original.Registered);
    }

    [Fact]
    public async Task TryReadAsync_NoSidecarOnDisk_ReturnsNull()
    {
        var dir = CreateTempDir();
        var lightPath = Path.Combine(dir, "never_registered.fits");

        var result = await RegistrationSidecar.TryReadAsync(lightPath, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task TryReadAsync_WrongLightPath_ReturnsNull()
    {
        // Simulate a sidecar that was written for one file and then the user
        // renamed the light. The sidecar's internal LightPath no longer
        // matches the path we're asking about -> treat as missing rather
        // than apply the wrong transform.
        var dir = CreateTempDir();
        var lightA = Path.Combine(dir, "lightA.fits");
        var lightB = Path.Combine(dir, "lightB.fits");

        var result = SampleResult(lightA);
        await RegistrationSidecar.WriteAsync(result, TestContext.Current.CancellationToken);
        File.Move(RegistrationSidecar.PathFor(lightA), RegistrationSidecar.PathFor(lightB));

        var loaded = await RegistrationSidecar.TryReadAsync(lightB, TestContext.Current.CancellationToken);

        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task TryReadAsync_CorruptJson_ReturnsNull()
    {
        var dir = CreateTempDir();
        var lightPath = Path.Combine(dir, "corrupt.fits");
        await File.WriteAllTextAsync(RegistrationSidecar.PathFor(lightPath), "{ not valid json :::", TestContext.Current.CancellationToken);

        var result = await RegistrationSidecar.TryReadAsync(lightPath, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task WriteAsync_OverwritesExistingSidecar()
    {
        var dir = CreateTempDir();
        var lightPath = Path.Combine(dir, "light.fits");

        await RegistrationSidecar.WriteAsync(SampleResult(lightPath, dx: 5f, dy: -3f), TestContext.Current.CancellationToken);
        await RegistrationSidecar.WriteAsync(SampleResult(lightPath, dx: 10f, dy: 7f), TestContext.Current.CancellationToken);

        var loaded = await RegistrationSidecar.TryReadAsync(lightPath, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.OffsetX.ShouldBe(10f, tolerance: 1e-6f);
        loaded.OffsetY.ShouldBe(7f, tolerance: 1e-6f);
    }

    [Fact]
    public async Task WriteAsync_AtomicViaTmp_DoesNotLeaveTmpBehind()
    {
        var dir = CreateTempDir();
        var lightPath = Path.Combine(dir, "light.fits");

        await RegistrationSidecar.WriteAsync(SampleResult(lightPath), TestContext.Current.CancellationToken);

        File.Exists(RegistrationSidecar.PathFor(lightPath)).ShouldBeTrue();
        File.Exists(RegistrationSidecar.PathFor(lightPath) + ".tmp").ShouldBeFalse();
    }

    // ---------- RegistrationResult ----------

    [Fact]
    public void Identity_ProducesIdentityMatrix()
    {
        var result = RegistrationResult.Identity("light.fits", registered: true);

        result.ToReference.IsIdentity.ShouldBeTrue();
        result.Registered.ShouldBeTrue();
    }

    [Fact]
    public void Identity_Unregistered_StarsMatchedZero()
    {
        var result = RegistrationResult.Identity("light.fits", registered: false);

        result.Registered.ShouldBeFalse();
        result.StarsMatched.ShouldBe(0);
    }

    [Fact]
    public void FromTransform_RoundTripsMatrix3x2()
    {
        var transform = new Matrix3x2(1.001f, 0.003f, -0.003f, 1.001f, 12.5f, -7.25f);
        var result = RegistrationResult.FromTransform("light.fits", transform, starsMatched: 30);

        result.ToReference.ShouldBe(transform);
        result.StarsMatched.ShouldBe(30);
        result.Registered.ShouldBeTrue();
    }
}
