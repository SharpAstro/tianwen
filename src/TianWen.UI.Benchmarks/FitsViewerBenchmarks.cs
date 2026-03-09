using BenchmarkDotNet.Attributes;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Benchmarks;

[MemoryDiagnoser]
[SimpleJob]
public class FitsViewerBenchmarks
{
    private FitsDocument _document = null!;

    [Params(
        @"C:\Users\SebastianGodelet\OneDrive\Dev\fits_tiff\LDN1089_singleFrame.fit"
    )]
    public string FilePath { get; set; } = "";

    [GlobalSetup]
    public async Task Setup()
    {
        _document = await FitsDocument.OpenAsync(FilePath)
            ?? throw new InvalidOperationException($"Failed to open: {FilePath}");
    }

    [Benchmark]
    public Task<FitsDocument?> Open()
    {
        return FitsDocument.OpenAsync(FilePath);
    }

    [Benchmark]
    public GpuStretchUniforms ComputeStretchUniforms_Linked()
    {
        return _document.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default);
    }

    [Benchmark]
    public GpuStretchUniforms ComputeStretchUniforms_Unlinked()
    {
        return _document.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default);
    }

    [Benchmark]
    public GpuStretchUniforms ComputeStretchUniforms_Luma()
    {
        return _document.ComputeStretchUniforms(StretchMode.Luma, StretchParameters.Default);
    }

    [Benchmark]
    public float[][] GetChannelArrays_Composite()
    {
        return _document.GetChannelArrays(ChannelView.Composite);
    }

    [Benchmark]
    public float[][] GetChannelArrays_SingleChannel()
    {
        return _document.GetChannelArrays(ChannelView.Red);
    }
}
