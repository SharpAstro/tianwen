using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Tests;

/// <summary>
/// Stand-in models for the tests of a unit scale above 1.0 (#2504) and of the enhance's one routine per step: a deblurrer
/// that lifts a star as BlurX does, and models that refuse a peak above 1.5 as GraXpert's and TianWen's ONNX models do.
/// </summary>
internal static class UnitScaleFakes
{
    /// <summary>The sky of a frame that is all sky but for its stars: its median.</summary>
    public static float SkyOf(Image image)
    {
        var samples = image.GetChannelSpan(0).ToArray();
        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    /// <summary>The guard GraXpert's background model and TianWen's ONNX denoiser, deconvolvers and star remover carry.</summary>
    public static Task<Image> GuardedCopy(Image input)
        => input.MaxValue > 1.5f
            ? throw new ArgumentException($"requires input normalised to ~[0, 1], got MaxValue={input.MaxValue}")
            : Task.FromResult(input.Clone());

    /// <summary>Lifts every value's height above the sky by a gain, as sharpening lifts a star's peak and leaves the sky,
    /// and stops at <c>[0, 1]</c>'s ceiling as BlurX does.</summary>
    public sealed class LiftingDeblurrer(float gain) : IImageDeblurrer
    {
        public string Name => "Test/Lifting";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var src = input.GetChannelSpan(0);
            var sky = SkyOf(input);
            var plane = new float[input.Height, input.Width];
            var max = float.NegativeInfinity;
            var min = float.PositiveInfinity;
            for (var y = 0; y < input.Height; y++)
            {
                for (var x = 0; x < input.Width; x++)
                {
                    var v = MathF.Min(1f, sky + (gain * (src[(y * input.Width) + x] - sky)));
                    plane[y, x] = v;
                    max = MathF.Max(max, v);
                    min = MathF.Min(min, v);
                }
            }
            return Task.FromResult(new Image([plane], BitDepth.Float32, max, min, 0f, input.ImageMeta));
        }
    }

    /// <summary>A denoiser with the ONNX models' guard (<see cref="GuardedCopy"/>).</summary>
    public sealed class GuardedDenoiser : IDenoiseEnhancer
    {
        public string Name => "Test/Guarded denoiser";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default) => GuardedCopy(input);
    }

    /// <summary>A star remover with TianWen's ONNX remover's guard (<see cref="GuardedCopy"/>): it keeps half of what it is
    /// handed as the starless plate.</summary>
    public sealed class GuardedRemover : IStarRemover
    {
        public string Name => "Test/Guarded remover";

        public async Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => (await GuardedCopy(input)).Multiply(0.5f);
    }

    /// <summary>A gradient model with GraXpert's guard (<see cref="GuardedCopy"/>); its name says GraXpert, so the gradient
    /// step's GraXpert backend takes it.</summary>
    public sealed class GuardedCorrector : IGradientCorrector
    {
        public string Name => "Test GraXpert";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default) => GuardedCopy(input);
    }
}
