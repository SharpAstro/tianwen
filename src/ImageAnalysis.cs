namespace Astap.Lib
{
    public static class ImageAnalysis
    {
        public static (int stars, double medianHFD, double medianFWHM, double background) AnalyseFITS(string file, double snr = 10.0, int maxStars = 500)
        {
            var stars = Native.analyse_fits(file, snr, maxStars, out var medianHFD, out var medianFWHM, out var background);

            return (stars, medianHFD, medianFWHM, background);
        }
    }
}