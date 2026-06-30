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

    /// <summary>AI backend: <c>auto</c> (default), <c>rc</c>, or <c>sas</c>. See <see cref="TianWen.Lib.Imaging.Enhancement.EnhanceBackend"/>.</summary>
    public string? Backend { get; init; }

    /// <summary>RC-Astro BlurXTerminator non-stellar sharpen (<c>bxt --sn</c>) in [0, 1]. Null = enhancer default.</summary>
    public float? DeblurSharpen { get; init; }

    /// <summary>RC-Astro NoiseXTerminator strength (<c>nxt --dn</c>) in [0, 1]. Null = noise-adaptive auto.</summary>
    public float? DenoiseStrength { get; init; }

    /// <summary>RC-Astro NoiseXTerminator iterations (<c>nxt --it</c>). Null = enhancer default.</summary>
    public int? DenoiseIterations { get; init; }
}
