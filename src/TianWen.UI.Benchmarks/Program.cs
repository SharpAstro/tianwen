using BenchmarkDotNet.Running;
using TianWen.UI.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(ImageBenchmarks).Assembly).Run(args);
