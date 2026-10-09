namespace TianWen.Hosting.Dto;

/// <summary>
/// Request body for <c>POST /api/v1/image/enhance</c>. The server enhances a FITS file that
/// already lives on its own disk (the headless rig writes captures locally), so the contract is
/// path-in / path-out -- mirroring the <c>tianwen image sharpen</c> CLI rather than uploading
/// pixels over HTTP. Backend + tuning fields feed <see cref="TianWen.Lib.Imaging.Enhancement.EnhanceOptions.TryParse"/>,
/// the same parser the CLI uses, so the knobs never drift.
/// </summary>
public sealed class EnhanceRequestDto
{
    /// <summary>Absolute path to the input FITS on the server's filesystem.</summary>
    public required string InputPath { get; init; }

    /// <summary>Output FITS path. When null, defaults to <c>&lt;input&gt;_enhanced.fits</c> next to the input.</summary>
    public string? OutputPath { get; init; }

    /// <summary>AI backend: <c>auto</c> (default), <c>rc</c>, or <c>tianwen</c>. See <see cref="TianWen.Lib.Imaging.Enhancement.EnhanceBackend"/>.</summary>
    public string? Backend { get; init; }

    /// <summary>Non-stellar deblur/deconvolution sharpen in [0, 1]; RC maps it to <c>bxt --sn</c>. Null = enhancer default.</summary>
    public float? DeblurSharpen { get; init; }

    /// <summary>Denoise strength in [0, 1]: RC maps it to <c>nxt --dn</c> (null = noise-adaptive auto); TianWen's own denoiser maps it to its blend dial (null = 1.0).</summary>
    public float? DenoiseStrength { get; init; }

    /// <summary>Denoiser iterations; RC maps it to <c>nxt --it</c>. Null = enhancer default.</summary>
    public int? DenoiseIterations { get; init; }

    /// <summary>The blur TianWen's own deconvolver removes, a Moffat FWHM in native pixels: one value or three
    /// (<c>red,green,blue</c>), as the CLI's <c>--deconv-kernel</c>. Null = none stated, and then that deconvolver does not
    /// serve (see <see cref="TianWen.Lib.Imaging.Enhancement.DeconvolutionKernel"/>).</summary>
    public string? DeconvKernel { get; init; }

    /// <summary>The Moffat beta of <see cref="DeconvKernel"/>; null = the default.</summary>
    public double? DeconvBeta { get; init; }

    /// <summary>The scale the frame is deconvolved at; null = the default.</summary>
    public double? DeconvResample { get; init; }
}
