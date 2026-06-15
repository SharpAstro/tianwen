using System;

namespace TianWen.Lib.Devices.Fake.Disturbance
{
    /// <summary>
    /// A 2-D Ornstein-Uhlenbeck (mean-reverting, time-correlated) process driving the RA and Dec
    /// components of a stochastic disturbance. Shared by the wind-gust and gear-noise terms, which
    /// differ only in amplitude and correlation time. Deterministic given the seed and the sequence
    /// of elapsed times it is advanced through; idempotent for repeated calls at the same elapsed.
    /// </summary>
    /// <remarks>
    /// The stationary variance equals amplitude^2: the diffusion per step is scaled by
    /// <c>sqrt(1 - decay^2)</c> so the process neither grows nor collapses over long runs.
    /// </remarks>
    internal sealed class OrnsteinUhlenbeck2D
    {
        private readonly double _amplitudeArcsec;
        private readonly double _decayTimeSeconds;
        private readonly int _seed;

        private Random _rng;
        private double _stateRa;
        private double _stateDec;
        private double _lastElapsed;

        public OrnsteinUhlenbeck2D(double amplitudeArcsec, double decayTimeSeconds, int seed)
        {
            _amplitudeArcsec = amplitudeArcsec;
            _decayTimeSeconds = decayTimeSeconds;
            _seed = seed;
            _rng = new Random(seed);
        }

        /// <summary>
        /// Advances the process to <paramref name="elapsedSeconds"/> and returns the current
        /// (RA, Dec) offset in arcsec. Only steps when time has increased since the last call, so a
        /// consumer that reads the model twice at the same instant sees a consistent value.
        /// </summary>
        public (double Ra, double Dec) Advance(double elapsedSeconds)
        {
            var dt = elapsedSeconds - _lastElapsed;
            if (dt > 0 && _decayTimeSeconds > 0)
            {
                var decay = Math.Exp(-dt / _decayTimeSeconds);
                var diffusion = _amplitudeArcsec * Math.Sqrt(1.0 - decay * decay);
                _stateRa = _stateRa * decay + diffusion * StochasticMath.NextGaussian(_rng);
                _stateDec = _stateDec * decay + diffusion * StochasticMath.NextGaussian(_rng);
                _lastElapsed = elapsedSeconds;
            }
            return (_stateRa, _stateDec);
        }

        public void Reset()
        {
            _rng = new Random(_seed);
            _stateRa = 0.0;
            _stateDec = 0.0;
            _lastElapsed = 0.0;
        }
    }
}
