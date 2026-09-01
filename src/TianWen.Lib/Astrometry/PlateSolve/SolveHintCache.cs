using System.Collections.Concurrent;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Astrometry.PlateSolve
{
    /// <summary>
    /// What a previous successful solve on the same light path already answered, so the next solve
    /// does not rediscover it: the plate SCALE the stars actually have, and which PARITY the optics
    /// present.
    /// </summary>
    /// <remarks>
    /// <para>Both are properties of the rig rather than of the frame, which is what makes them
    /// cacheable at all. They are also the two inputs the seed is most expensive without: the scale
    /// sets the width of the window every hypothesis draws its candidate pairs from (a star-recovered
    /// scale is 7.4x fewer hypotheses than a typed <c>FOCALLEN</c>), and the parity decides which half
    /// of the race is pure waste (97% of the seed's hypotheses, measured over 96 frozen Vela frames).
    /// </para>
    /// <para><b>Everything here is a HINT and nothing is a constraint.</b> A stale entry -- someone
    /// added a diagonal, moved the camera to the OAG port, swapped a reducer, changed capture software
    /// -- must never turn a solvable frame into a failure. So the scale is offered as the narrow tier
    /// of a fallback chain that already exists, and the parity only shrinks the OTHER half's
    /// hypothesis budget rather than skipping it, which keeps both halves running and leaves the
    /// acceptance gate's re-run as the backstop. A miss costs one wider pass and corrects itself on
    /// the next successful solve.</para>
    /// <para>Process-lifetime and in-memory: a session solves dozens of frames on one rig, so the
    /// first solve pays and the rest do not. Persisting it across restarts the way
    /// <c>BacklashHistory</c> is persisted would extend that to the first solve of a night, and is
    /// deliberately left out of this change -- it needs a path, a serializer context and an
    /// <c>IExternal</c> the solver does not currently take.</para>
    /// </remarks>
    internal sealed class SolveHintCache
    {
        /// <summary>
        /// The LIGHT PATH, which is what parity and scale are properties of -- never the rig, and
        /// never either half alone.
        /// </summary>
        /// <remarks>
        /// <para>Parity is set by the number of reflections between sky and sensor plus the sensor's
        /// own row-order convention, and those live on different objects. An off-axis guider's
        /// pick-off prism is one reflection before the imaging plane, so a refractor + OAG has an
        /// even-parity main camera and an ODD-parity guide camera at the same instant on the same rig
        /// -- and the polar-align loop solves guider frames while the session solves imaging frames,
        /// so one answer per rig is actively wrong for one of them. Equally it is not per camera:
        /// moving one body from a refractor to an SCT with a diagonal changes its parity, while
        /// swapping bodies on one scope changes nothing.</para>
        /// <para><see cref="RowOrder"/> is in the key rather than left to be learned because it is a
        /// parity determinant in its own right -- a <c>BOTTOM-UP</c> frame read as <c>TOP-DOWN</c> is
        /// a vertical flip with no mirror anywhere in the optics -- and capture software differs. In
        /// the key it can never mispredict; learned, it would cost a fallback every time it changed.
        /// </para>
        /// <para>Binning is in the key for the SCALE's sake, not parity's: the same camera at 1x1 and
        /// 2x2 is two different plate scales, and a ratio learned at one would be a wrong prior at the
        /// other.</para>
        /// </remarks>
        internal readonly record struct LightPath(string Telescope, string Instrument, RowOrder RowOrder, int BinX, int BinY)
        {
            internal static LightPath From(in ImageMeta meta)
                => new LightPath(meta.Telescope ?? "", meta.Instrument ?? "", meta.RowOrder, meta.BinX, meta.BinY);

            /// <summary>
            /// Whether this frame says enough about its own optics to be worth remembering. An
            /// unnamed telescope AND camera is not a light path, it is every frame that omits the
            /// keywords -- and collapsing those into one entry would hand a synthetic frame's answer
            /// to a real rig.
            /// </summary>
            internal bool IsIdentified => Telescope.Length > 0 || Instrument.Length > 0;
        }

        /// <param name="ScaleRatio">
        /// Header-implied plate scale divided by the solved one, i.e. the same quantity
        /// <see cref="QuadScaleRecovery.Recovery.Ratio"/> carries, and consumed through the same path.
        /// A RATIO rather than an absolute scale so it stays meaningful when the header's own numbers
        /// move: it records how wrong the typed focal length is, which is the part that persists.
        /// </param>
        /// <param name="WinnerIsStd">Which parity the accepted solve came from.</param>
        internal readonly record struct Hint(float ScaleRatio, bool WinnerIsStd);

        private readonly ConcurrentDictionary<LightPath, Hint> _hints = new();

        /// <summary>What a previous accepted solve on this light path answered, if there was one.</summary>
        internal Hint? TryGet(in ImageMeta meta)
        {
            var key = LightPath.From(meta);
            return key.IsIdentified && _hints.TryGetValue(key, out var hint) ? hint : null;
        }

        /// <summary>
        /// Records what an ACCEPTED solve answered. Only ever called for a WCS that cleared the
        /// acceptance gate: a rejected solve's parity and scale are whatever noise it latched onto,
        /// and caching those would make the next solve slower AND point it at the wrong window.
        /// </summary>
        internal void Store(in ImageMeta meta, float scaleRatio, bool winnerIsStd)
        {
            var key = LightPath.From(meta);
            if (key.IsIdentified && float.IsFinite(scaleRatio) && scaleRatio > 0)
            {
                _hints[key] = new Hint(scaleRatio, winnerIsStd);
            }
        }

        /// <summary>Test seam: how many light paths have been learned.</summary>
        internal int Count => _hints.Count;
    }
}
