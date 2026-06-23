using System;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

public class ComplexFftTests
{
    [Fact]
    public void Forward_then_inverse_round_trips()
    {
        var rng = new Random(1234);
        var original = new Complex[16];
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
        }

        var work = (Complex[])original.Clone();
        ComplexFft.Forward(work);
        ComplexFft.Inverse(work);

        for (var i = 0; i < original.Length; i++)
        {
            work[i].Real.ShouldBe(original[i].Real, 1e-9);
            work[i].Imaginary.ShouldBe(original[i].Imaginary, 1e-9);
        }
    }

    [Fact]
    public void Constant_input_gives_dc_only_spectrum()
    {
        var data = new Complex[8];
        Array.Fill(data, Complex.One);

        ComplexFft.Forward(data);

        data[0].Real.ShouldBe(8.0, 1e-9); // sum of all samples
        data[0].Imaginary.ShouldBe(0.0, 1e-9);
        for (var k = 1; k < data.Length; k++)
        {
            data[k].Magnitude.ShouldBe(0.0, 1e-9);
        }
    }

    [Fact]
    public void Delta_input_gives_flat_unit_spectrum()
    {
        var data = new Complex[8];
        data[0] = Complex.One;

        ComplexFft.Forward(data);

        for (var k = 0; k < data.Length; k++)
        {
            data[k].Magnitude.ShouldBe(1.0, 1e-9);
        }
    }

    [Fact]
    public void Non_power_of_two_throws()
    {
        var data = new Complex[6];
        Should.Throw<ArgumentException>(() => ComplexFft.Forward(data));
    }
}
