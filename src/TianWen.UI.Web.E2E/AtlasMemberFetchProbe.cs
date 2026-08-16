using System.Diagnostics;
using Microsoft.Playwright;
using Shouldly;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Watches what the atlas actually asks the network for, now that the catalog is published as
/// region-aligned members (docs/plans/web-tycho2.md). The question this answers is the one the plan
/// left open: does a first open fetch a fraction of the sky, and does panning pick up the rest.
///
/// <para><b>Counts and bytes, never milliseconds.</b> A dev server is interpreted, where compute
/// runs 24-42x slower than the deployed AOT build, so a duration measured here means nothing
/// anywhere else. Request counts and payload sizes are properties of the design and transfer
/// exactly.</para>
///
/// <para><b>It needs a Lightweight server with the members staged</b>, because a default build
/// embeds the whole catalog in the assembly and <c>ReadTycho2Bulk</c> loads it at init -- the DB
/// would already be full and every fetch path would be a no-op that looks like success:</para>
/// <code>
/// dotnet run --project tools/bake-tycho2/BakeTycho2.csproj -c Release -- \
///   src/TianWen.Lib/Astrometry/Catalogs/tyc2.bin.lz src/TianWen.UI.Web/wwwroot/tyc2
/// cd src/TianWen.UI.Web &amp;&amp; dotnet run -c Release -p:Lightweight=true --urls http://localhost:5099
/// TIANWEN_WEB_BASEURL=http://localhost:5099 TIANWEN_WEB_PROBE=1 dotnet test TianWen.UI.Web.E2E \
///   --filter FullyQualifiedName~AtlasMemberFetchProbe
/// </code>
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class AtlasMemberFetchProbe(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float LoadTimeout = 180_000;

    [Fact]
    public async Task MeasureAsync()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_WEB_PROBE") == "1",
            "probe: set TIANWEN_WEB_PROBE=1 (and point TIANWEN_WEB_BASEURL at a Lightweight server with tyc2/ staged)");

        var page = await fixture.NewPageAsync();
        var console = new List<string>();
        page.Console += (_, m) => { lock (console) console.Add(m.Text); };

        // Sizes come from the RESPONSE, not from a local file listing: the point is what crossed the
        // wire, including anything the host added on the way.
        var fetched = new List<(string Url, int Bytes)>();
        page.Response += (_, r) =>
        {
            if (!r.Url.Contains("/tyc2/", StringComparison.Ordinal)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var body = await r.BodyAsync();
                    lock (fetched) fetched.Add((r.Url[(r.Url.LastIndexOf('/') + 1)..], body.Length));
                }
                catch { /* a response body can be gone by the time we ask; the count still lands */ }
            });
        };

        var sw = Stopwatch.StartNew();
        await page.GotoAsync(fixture.BaseUrl + "?view=sky",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = LoadTimeout });

        // The app's own line is the completion signal: it prints once members have landed and the
        // star field has been rebuilt. A DOM state would be true before the load starts as well.
        var landed = await WaitForConsoleAsync(console, "tyc2 members:", LoadTimeout);
        Assert.True(landed, $"no members landed within {LoadTimeout} ms");
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        Report("first open", sw.ElapsedMilliseconds, fetched);
        var afterOpen = Count(fetched);

        // Now move somewhere else entirely and confirm the client goes and gets that sky too.
        await page.EvaluateAsync("() => window.scrollTo(0, 0)");
        await page.Mouse.MoveAsync(600, 400);
        await page.Mouse.DownAsync();
        for (var i = 0; i < 8; i++)
        {
            await page.Mouse.MoveAsync(600 - (i * 60), 400 + (i * 20));
            await Task.Delay(40, TestContext.Current.CancellationToken);
        }
        await page.Mouse.UpAsync();
        await Task.Delay(4000, TestContext.Current.CancellationToken);

        Report("after a pan", sw.ElapsedMilliseconds, fetched);
        var afterPan = Count(fetched);
        var rebuilds = CountLines(console, "tyc2 flatten");

        lock (console)
        {
            foreach (var line in console.Where(c => c.Contains("tyc2") || c.Contains("sky geometry")))
            {
                output.WriteLine($"[members]   {line}");
            }
        }

        afterOpen.Files.ShouldBeLessThan(166, "a first open must not fetch the whole catalog");
        afterOpen.Files.ShouldBeGreaterThan(1, "the header alone is not a sky");
        afterPan.Files.ShouldBeGreaterThanOrEqualTo(afterOpen.Files, "panning must never un-fetch");

        // The rebuild is the expensive part (a full re-walk of 2.5M offsets, a regroup, and a whole
        // instance-buffer re-upload) and it does NOT get cheaper for happening more often, since it
        // always covers every member held. Before the debounce this pan cost six of them, one per
        // quantized view cell it crossed; the count is asserted rather than the duration because a
        // duration measured on an interpreted dev server means nothing on the deployed build.
        output.WriteLine($"[members] rebuilds: {rebuilds} (one for the first open, then per settle)");
        rebuilds.ShouldBeGreaterThan(0, "no rebuild at all means the stars never reached the GPU");
        rebuilds.ShouldBeLessThanOrEqualTo(4,
            "a pan is re-flattening per view cell again; the flatten debounce has regressed");
    }

    private static int CountLines(List<string> console, string marker)
    {
        lock (console) return console.Count(c => c.Contains(marker, StringComparison.Ordinal));
    }

    private (int Files, long Bytes) Count(List<(string Url, int Bytes)> fetched)
    {
        lock (fetched) return (fetched.Count, fetched.Sum(f => (long)f.Bytes));
    }

    private void Report(string stage, long elapsedMs, List<(string Url, int Bytes)> fetched)
    {
        var (files, bytes) = Count(fetched);
        output.WriteLine($"[members] {stage}: {files} files, {bytes / (1024.0 * 1024.0):F2} MiB "
            + $"(wall {elapsedMs} ms -- INTERPRETED, not comparable to the deployed build)");
    }

    private static async Task<bool> WaitForConsoleAsync(List<string> console, string marker, float timeoutMs)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            lock (console)
            {
                if (console.Any(c => c.Contains(marker, StringComparison.Ordinal))) return true;
            }
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return false;
    }
}
