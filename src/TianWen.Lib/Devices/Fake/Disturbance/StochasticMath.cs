using System;

namespace TianWen.Lib.Devices.Fake.Disturbance
{
    /// <summary>Shared random helpers for stochastic disturbance terms.</summary>
    internal static class StochasticMath
    {
        /// <summary>Box-Muller transform for a standard-normal variate from a seeded RNG.</summary>
        public static double NextGaussian(Random rng)
        {
            var u1 = rng.NextDouble();
            var u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
