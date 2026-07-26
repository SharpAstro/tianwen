namespace TianWen.Lib.Sequencing
{
    /// <summary>
    /// What the session answers a user prompt with when <b>nobody can answer it</b> -- no interactive
    /// handler subscribed to <see cref="ISessionTelemetry.PromptRequested"/>, or every observer gone.
    /// <para>
    /// <b>Why this is a choice and not just "proceed".</b> Every prompt today gates a <i>physical</i>
    /// prerequisite ("switch on the flat panel", and in the planned dark-frame flow "cover the scope").
    /// Answering <see cref="Proceed"/> therefore asserts a fact about the world that demonstrably did not
    /// happen: nobody was there to act. For flats we get away with it because
    /// <c>FlatExposureSolver</c> fails the metering and the OTA is skipped -- but a dark-frame prompt has
    /// no such backstop, and light-leaked darks would be written as valid calibration and then subtracted
    /// from every light. Declining skips the gated step instead, which is honest: missing calibration is
    /// recoverable, silently wrong calibration is not.
    /// </para>
    /// <para>
    /// <b>Why not simply block until a human appears.</b> The prompt await sits inside
    /// <c>RunAsync</c>'s try, whose finally is what parks the mount, warms the cameras and closes the
    /// covers. A prompt nothing ever answers does not throw -- it just never returns -- so the rig would
    /// sit unparked with the covers open at dawn. Both extremes are unsafe; this picks per caller.
    /// </para>
    /// </summary>
    public enum UnattendedPromptResponse
    {
        /// <summary>
        /// Skip the gated step (the default). Correct for anything nobody asked for at 4am -- above all
        /// the end-of-session flat block, which runs on a schedule rather than on request.
        /// </summary>
        Decline = 0,

        /// <summary>
        /// Proceed as though a human confirmed. Only defensible when the run itself was <b>explicitly
        /// invoked by an operator</b> who may well have prepared the hardware and then walked back inside
        /// -- <c>tianwen flats</c> and <c>POST /api/v1/session/flats</c> are exactly that case, and they
        /// opt in through their own <see cref="SessionConfiguration"/>. Never a sensible default for a
        /// scheduled run.
        /// </summary>
        Proceed = 1,
    }
}
