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

    /// <summary>
    /// Let the viewer pick from the frame itself. A UI-level intent, NOT a GPU mode: it is resolved to
    /// a concrete algorithm before the viewer's <c>GpuDebayerMode</c> is asked,
    /// so it never reaches the shader (which only ever sees None/BilinearMono/MHC/VNG). Deliberately
    /// LAST, like <see cref="StretchMode.Auto"/> and for the same reason: the numeric values already
    /// written into serialized viewer state stay put.
    /// Resolution: <see cref="DebayerAlgorithmExtensions.ResolveAuto"/>.
    /// </summary>
    Auto,
}
