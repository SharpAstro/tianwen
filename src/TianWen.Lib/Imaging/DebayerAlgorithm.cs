using System.ComponentModel;

namespace TianWen.Lib.Imaging;

public enum DebayerAlgorithm
{
    None,
    BilinearMono,
    [Description("Variable number of gradients")]
    VNG,
    [Description("Adaptive homogeneity-directed demosaicing")]
    AHD,
}