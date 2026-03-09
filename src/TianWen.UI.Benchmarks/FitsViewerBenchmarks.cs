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
    public Task Reprocess_Linked()
    {
        var state = new ViewerState { StretchMode = StretchMode.Linked };
        return ViewerActions.ReprocessAsync(_document, state);
    }

    [Benchmark]
    public Task Reprocess_Unlinked()
    {
        var state = new ViewerState { StretchMode = StretchMode.Unlinked };
        return ViewerActions.ReprocessAsync(_document, state);
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
