using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

public static class DebayerAlgorithmExtensions
{
    extension(DebayerAlgorithm algorithm)
    {
        public string DisplayName => algorithm switch
        {
            DebayerAlgorithm.BilinearMono => "Mono",
            _ => algorithm.ToString(),
        };
    }
}
