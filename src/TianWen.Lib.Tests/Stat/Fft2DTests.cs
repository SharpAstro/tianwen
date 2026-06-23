using System;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

public class Fft2DTests
{
    [Fact]
    public void Forward_then_inverse_round_trips_2d()
    {
        const int w = 8, h = 4;
        var rng = new Random(99);
        var original = new Complex[w * h];
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = new Complex(rng.NextDouble(), rng.NextDouble() - 0.5);
        }

        var work = (Complex[])original.Clone();
        Fft2D.Forward(work, w, h);
        Fft2D.Inverse(work, w, h);

        for (var i = 0; i < original.Length; i++)
        {
            work[i].Real.ShouldBe(original[i].Real, 1e-9);
            work[i].Imaginary.ShouldBe(original[i].Imaginary, 1e-9);
        }
    }

    [Fact]
    public void Delta_at_origin_gives_flat_unit_spectrum()
    {
        const int w = 8, h = 8;
        var data = new Complex[w * h];
        data[0] = Complex.One;

        Fft2D.Forward(data, w, h);

        foreach (var c in data)
        {
            c.Magnitude.ShouldBe(1.0, 1e-9);
        }
    }

    [Fact]
    public void Constant_gives_dc_only()
    {
        const int w = 4, h = 4;
        var data = new Complex[w * h];
        Array.Fill(data, Complex.One);

        Fft2D.Forward(data, w, h);

        data[0].Real.ShouldBe(16.0, 1e-9); // sum of all samples
        for (var i = 1; i < data.Length; i++)
        {
            data[i].Magnitude.ShouldBe(0.0, 1e-9);
        }
    }

    [Fact]
    public void Rejects_non_power_of_two_dimension()
    {
        Should.Throw<ArgumentException>(() => Fft2D.Forward(new Complex[6 * 4], 6, 4));
    }
}
