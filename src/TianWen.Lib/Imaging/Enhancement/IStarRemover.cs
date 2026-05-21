namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Marker interface for image enhancers that produce a *starless* image from
/// the source -- the stars have been removed (in-painted to the local
/// background level) leaving only nebula / galaxy / dust / sky structure.
/// </summary>
/// <remarks>
/// <para>Used by the <see cref="SharpenPipeline"/> orchestrator (planned) to
/// split a frame into a starless plate and a stars-only plate
/// (<c>StarsOnly = Source - Starless</c>) so the stellar and non-stellar
/// components can be processed independently. Also useful standalone as a
/// "starless export" feature for users doing manual processing in tools like
/// PixInsight.</para>
///
/// <para>Implementations should accept any source colour space and produce
/// output in the same units (linear in -> linear out, stretched in ->
/// stretched out). The output's <i>units</i> match the input, but the
/// transformation applied is <i>not</i> a linear-domain function of the
/// input -- star removal globally rewrites the histogram (stellar pixels
/// collapse to the local nebula level). See PLAN-ai-enhancement.md "Domain
/// semantics" for the implications when chaining with other linear-domain
/// tools.</para>
/// </remarks>
public interface IStarRemover : IImageEnhancer
{
}
