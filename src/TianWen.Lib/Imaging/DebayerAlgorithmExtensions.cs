namespace TianWen.Lib.Imaging;

public static class DebayerAlgorithmExtensions
{
    extension(DebayerAlgorithm algorithm)
    {
        public string DisplayName => algorithm switch
        {
            DebayerAlgorithm.BilinearMono => "Mono",
            _ => algorithm.ToString(),
        };

        /// <summary>
        /// Resolves <see cref="DebayerAlgorithm.Auto"/> to a concrete algorithm; returns any other
        /// algorithm unchanged.
        ///
        /// <para>A CFA mosaic is the only case where the choice does anything at all, since every other
        /// input takes a different shader path entirely. It still answers for them, because the selector
        /// SHOWS the resolved value and "VNG" on a mono frame claims work nobody is doing.</para>
        ///
        /// <para><b>Why a video stream gets MHC and a still gets VNG.</b> VNG is the default on a
        /// measurement taken AT STARS: over four stars of a real CR3 the halo rim sits 0.36% below the
        /// local sky for VNG against 2.86% above for MHC, which is a dark ring around every bright core.
        /// A planetary capture has no stars, so that measurement does not transfer, while
        /// <c>PlanetaryMaster</c> demosaics its own output with MHC. Matching it means the frame being
        /// scrubbed looks like the master the stacker will build. Parity, NOT speed: if this is ever
        /// re-argued as a performance choice it will be optimised into the wrong answer.</para>
        /// </summary>
        /// <param name="isBayerMosaic">One channel carrying a CFA pattern, the only input a demosaic acts on.</param>
        /// <param name="isColour">Three or more channels, i.e. already demosaiced or natively colour.</param>
        /// <param name="isVideoStream">A planetary/lunar video source (SER) rather than a still frame.</param>
        public DebayerAlgorithm ResolveAuto(bool isBayerMosaic, bool isColour, bool isVideoStream)
            => algorithm is not DebayerAlgorithm.Auto ? algorithm
                : isBayerMosaic ? (isVideoStream ? DebayerAlgorithm.MHC : DebayerAlgorithm.VNG)
                : isColour ? DebayerAlgorithm.None
                : DebayerAlgorithm.BilinearMono;

        /// <summary>
        /// The same resolution, reading the two frame facts off the image so the
        /// "one channel carrying a CFA" predicate is written once rather than at every call site.
        /// </summary>
        /// <param name="image">The frame as it will be displayed or saved.</param>
        /// <param name="isVideoStream">See the overload above; a still document is never one.</param>
        public DebayerAlgorithm ResolveAuto(Image image, bool isVideoStream = false)
            => algorithm.ResolveAuto(
                isBayerMosaic: image.ImageMeta.SensorType is SensorType.RGGB && image.ChannelCount == 1,
                isColour: image.ChannelCount >= 3,
                isVideoStream: isVideoStream);
    }
}
