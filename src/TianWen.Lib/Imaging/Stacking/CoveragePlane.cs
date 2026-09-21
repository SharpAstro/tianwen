using TianWen.Lib.Imaging;

namespace TianWen.Lib.Imaging.Stacking
{
    /// <summary>
    /// What a coverage plane IS, in one place: a single-channel image whose pixel is how many frames
    /// put a finite sample there, averaged over the channels, on a scale whose maximum is the frame
    /// count. Every strategy that can answer that question answers it through here.
    /// </summary>
    /// <remarks>
    /// <para>It exists because the alternative is each strategy finalising its own, which is four
    /// copies of the same four arguments and no file to review when one of them drifts. The scale
    /// matters and is easy to get wrong in a copy: <c>maxValue</c> is the FRAME COUNT, not 1, so the
    /// exact crop tier can tell a pixel eleven frames reached from one that two did, and a plane
    /// written with <c>maxValue: 1</c> reads as fully covered everywhere.</para>
    ///
    /// <para>A drizzle strategy does not come through here and should not: its weight map already IS
    /// the coverage, it says so with <see cref="IntegrationResult.RejectionMapIsCoverage"/>, and the
    /// sidecar it writes carries the same fact under the same <c>MAPKIND</c>. Two ways to express one
    /// thing is the surface worth removing next; this removes the four.</para>
    /// </remarks>
    internal static class CoveragePlane
    {
        /// <summary>The per-pixel counts a sink accumulated, as a coverage image.</summary>
        internal static Image Finalise(IIntegrationSink sink, int frameCount, in ImageMeta meta)
            => sink.FinaliseAsImage(
                BitDepth.Float32,
                maxValue: frameCount,
                minValue: 0f,
                pedestal: 0f,
                meta: meta);

        /// <summary>The same, for a strategy that assembled the plane itself from per-strip
        /// coverage it already had.</summary>
        internal static Image FromPlane(float[][,] data, int frameCount, in ImageMeta meta)
            => new Image(
                data: data,
                bitDepth: BitDepth.Float32,
                maxValue: frameCount,
                minValue: 0f,
                pedestal: 0f,
                imageMeta: meta);

        /// <summary>
        /// The same, for a strategy that already holds the count as a plain array. Nothing is
        /// recounted: a two-pass strategy tallies contributing samples to compute its mean and can
        /// simply hand that tally over.
        /// </summary>
        internal static Image FromCounts(uint[,] counts, int channelCount, int frameCount, in ImageMeta meta)
        {
            var height = counts.GetLength(0);
            var width = counts.GetLength(1);
            var data = Image.CreateChannelData(1, height, width);
            var plane = data[0];
            var inv = channelCount > 0 ? 1f / channelCount : 1f;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    plane[y, x] = counts[y, x] * inv;
                }
            }

            return new Image(
                data: data,
                bitDepth: BitDepth.Float32,
                maxValue: frameCount,
                minValue: 0f,
                pedestal: 0f,
                imageMeta: meta);
        }
    }
}
