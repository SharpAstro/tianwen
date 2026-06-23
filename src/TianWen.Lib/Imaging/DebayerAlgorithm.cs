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
    // Appended last so the existing numeric values stay stable for any serialized profile state.
    [Description("Malvar-He-Cutler gradient-corrected linear")]
    MHC,
}