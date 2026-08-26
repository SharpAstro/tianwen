namespace TianWen.Lib.Imaging.Stacking
{
    /// <summary>
    /// What a Bayer frame is reshaped into before <c>--remove-stars</c> hands it to the star remover.
    ///
    /// <remarks>
    /// <para>The two modes trade against each other and there is no setting that wins both ways,
    /// which is why this is a knob rather than a fix. Measured on the 10P/Tempel 2 set, 135 frames,
    /// comet-aligned: whole-mosaic removal leaves coloured streaks (per-frame residual tails of
    /// R +15.94 / G +5.22 / B +3.98 sigma with green digging -6.35 sigma holes, which is
    /// red-positive-green-negative, i.e. MAGENTA) but keeps 85.7% of the comet's integrated flux.
    /// Splitting first removes the streaks outright (the same frame gives 5.73 / 5.67 / 4.81 with
    /// holes of -3.71 / -3.61 / -3.68, and in the master red's residue falls 88% while green's
    /// residual turns positive, so the magenta is gone) and keeps 22.4% of the comet -- it punches
    /// the nucleus out and leaves a visible donut.</para>
    /// <para>The cause is scale. Splitting halves the raster, so this comet's 9 px HWHM coma becomes
    /// 4.5 px, which is a star profile as far as the model is concerned. Demosaicing to full-res RGB
    /// instead does NOT rescue it: on the comet-aligned master that removed 100% of the comet, worse
    /// than splitting. Comet survival tracks how badly INTERLEAVED the input is, which suggests the
    /// checkerboard is simultaneously the cause of the magenta and the reason the coma survives at
    /// all -- a mosaic confuses the model too much for it to commit to calling the coma a star.</para>
    /// <para>So the choice is really about whether the frame contains an extended object worth
    /// protecting. It usually does not, and <see cref="SplitCfa"/> is the better remover; a comet
    /// layer is exactly the case where it is not.</para>
    /// </remarks>
    /// </summary>
    public enum StarRemovalMode
    {
        /// <summary>
        /// Hand the remover the raw CFA mosaic whole. Leaves channel-asymmetric coloured residue on
        /// the star trails, and preserves extended objects. The default, because the residue is
        /// cosmetic and largely answered by a walking-noise denoise plus a background curve, whereas
        /// a nucleus the remover ate is not recoverable at any later stage.
        /// </summary>
        Mosaic,

        /// <summary>
        /// Split the mosaic into its four half-resolution photosite planes and remove stars from each
        /// separately. A star in a mosaic is a checkerboard rather than a point spread function, and
        /// each plane is an ordinary smooth image, so the coloured residue disappears. Choose it when
        /// the frame holds no extended target -- anything the size of a star, at half resolution, is
        /// removed, and that includes a small comet's coma.
        /// </summary>
        SplitCfa,
    }
}
