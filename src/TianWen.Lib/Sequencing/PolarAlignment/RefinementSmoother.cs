using System;

namespace TianWen.Lib.Sequencing.PolarAlignment
{
    /// <summary>
    /// Steady-state filter for Phase B refinement. Two responsibilities:
    ///
    /// <list type="number">
    /// <item><description><b>Smooth</b> the displayed error so a single noisy
    /// solve (mount jitter, transient cloud, marginal star count) doesn't
    /// bounce the gauges. EWMA with alpha = 2 / (N + 1) where N is the
    /// configured window size.</description></item>
    /// <item><description><b>Settled flag</b>: true when the magnitude
    /// standard deviation across the recent window drops below the configured
    /// sigma. Settled means the user has stopped turning the knobs — does
    /// not imply alignment is good.</description></item>
    /// </list>
    ///
    /// Stateful per-routine instance; one created at the start of Phase B,
    /// fed each successful live solve, queried for the smoothed result.
    /// </summary>
    internal sealed class RefinementSmoother
    {
        private readonly int _window;
        private readonly double _settleSigmaRad;
        private readonly double _alpha;

        // Circular buffers of signed az / alt (radians) for per-axis variance.
        // Magnitude alone hides sign flips: a user wobbling Az from +5' to -5'
        // produces constant magnitude but is plainly not settled.
        private readonly double[] _bufAz;
        private readonly double[] _bufAlt;
        private int _samplesSeen;
        private int _writeIdx;

        // Running EWMAs of az/alt (radians).
        private double _ewmaAz;
        private double _ewmaAlt;
        private bool _initialised;

        public RefinementSmoother(int window, double settleSigmaArcmin)
        {
            if (window < 1) window = 1;
            _window = window;
            _settleSigmaRad = settleSigmaArcmin * Math.PI / (180.0 * 60.0);
            _alpha = 2.0 / (window + 1.0);
            _bufAz = new double[window];
            _bufAlt = new double[window];
        }

        /// <summary>
        /// Add a fresh raw error sample. Returns the smoothed (az, alt) and
        /// the settled flag for the current state.
        /// </summary>
        public (double SmoothedAzRad, double SmoothedAltRad, bool IsSettled) Update(double rawAzRad, double rawAltRad)
        {
            // EWMA. Initialise to first sample so we don't drag in zero for the first ticks.
            if (!_initialised)
            {
                _ewmaAz = rawAzRad;
                _ewmaAlt = rawAltRad;
                _initialised = true;
            }
            else
            {
                _ewmaAz = _alpha * rawAzRad + (1.0 - _alpha) * _ewmaAz;
                _ewmaAlt = _alpha * rawAltRad + (1.0 - _alpha) * _ewmaAlt;
            }

            _bufAz[_writeIdx] = rawAzRad;
            _bufAlt[_writeIdx] = rawAltRad;
            _writeIdx = (_writeIdx + 1) % _window;
            if (_samplesSeen < _window) _samplesSeen++;

            bool settled = false;
            if (_samplesSeen >= _window)
            {
                // Per-axis sample variance. Both axes must be quiet for "settled".
                double meanAz = 0, meanAlt = 0;
                for (int i = 0; i < _window; i++) { meanAz += _bufAz[i]; meanAlt += _bufAlt[i]; }
                meanAz /= _window; meanAlt /= _window;
                double varAz = 0, varAlt = 0;
                for (int i = 0; i < _window; i++)
                {
                    var dA = _bufAz[i] - meanAz;
                    var dE = _bufAlt[i] - meanAlt;
                    varAz += dA * dA;
                    varAlt += dE * dE;
                }
                varAz /= _window; varAlt /= _window;
                settled = Math.Sqrt(varAz) < _settleSigmaRad && Math.Sqrt(varAlt) < _settleSigmaRad;
            }

            return (_ewmaAz, _ewmaAlt, settled);
        }
    }
}
