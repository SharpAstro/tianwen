using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Xunit;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// The atlas's IndexedDB member cache for a visitor who comes back after the catalog was re-baked
/// (docs/plans/web-tycho2.md, "The member cache names each member's content").
///
/// <para>On 2026-10-05 the deployed atlas drew stars only around Pegasus. The 2026-09-25 re-bake had
/// changed the length of 151 of 166 members, the cache key had not changed, and every one of those
/// came back from the cache at its old length, was refused without a word, and was never fetched
/// again: each view change read them all once more, and leaked them. The cache now keys a member by
/// the CRC32 the manifest gives it, and a refused entry is fetched from the network.</para>
///
/// <para>Needs a server with the members staged (see <see cref="AtlasMemberFetchProbe"/>'s remarks for
/// the bake); it skips on one without them. Counts and console lines, never milliseconds: the dev
/// server is interpreted.</para>
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed partial class AtlasMemberCacheTests(TianWenWebFixture fixture, ITestOutputHelper output)
{
    /// <summary>The app's cache version (Planner.razor, <c>Tyc2CacheVersion</c>), which prefixes every key.</summary>
    private const string CacheVersion = "tyc2-v3-raw";

    [GeneratedRegex(@"tyc2 members: \+(\d+) of (\d+) \((\d+) cached\)")]
    private static partial Regex MembersLine();

    [Fact(Timeout = 900_000)]
    public async Task AReturningVisitorsStaleMembersAreFetchedAgainAndTheNextVisitReadsThemFromTheCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var page = await fixture.NewPageAsync();
        var console = new ConcurrentQueue<string>();
        page.Console += (_, m) => console.Enqueue(m.Text);

        var manifest = await page.APIRequest.GetAsync(fixture.BaseUrl + "tyc2/manifest.bin");
        Assert.SkipUnless(manifest.Ok, "no Tycho-2 members staged on this server");
        var tags = Tags(await manifest.BodyAsync());

        // What a returning visitor holds, written on the app's origin before the app runs: an entry in
        // the format before members carried a tag, one under a tag no bake gives that member now, and
        // every member under its CURRENT tag but holding bytes of the wrong length, which is what a
        // refused entry looks like whatever refused it.
        await page.GotoAsync(fixture.BaseUrl + "css/app.css");
        await page.EvaluateAsync("""
            async ({ version, tags }) => {
                const db = await new Promise((resolve, reject) => {
                    const req = indexedDB.open("tianwen-atlas", 1);
                    req.onupgradeneeded = () => req.result.createObjectStore("tyc2");
                    req.onsuccess = () => resolve(req.result);
                    req.onerror = () => reject(req.error);
                });
                await new Promise((resolve, reject) => {
                    const tx = db.transaction("tyc2", "readwrite");
                    const store = tx.objectStore("tyc2");
                    const junk = new Uint8Array(10).buffer;
                    store.put(junk, "tyc2-v2-raw:m5");
                    store.put(junk, version + ":m6:deadbeef");
                    for (let m = 1; m < tags.length; m++) store.put(junk, version + ":m" + m + ":" + tags[m]);
                    tx.oncomplete = () => resolve();
                    tx.onerror = () => reject(tx.error);
                });
                db.close();
            }
            """, new { version = CacheVersion, tags });

        await page.GotoAsync(fixture.BaseUrl + "?view=sky", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var first = await WaitForLineAsync(console, line => MembersLine().IsMatch(line), ct);
        var swept = await WaitForLineAsync(console, line => line.Contains("tyc2 member cache: dropped", StringComparison.Ordinal), ct);
        output.WriteLine(first);
        output.WriteLine(swept);
        var (landed, wanted, cached) = Parse(first);

        swept.ShouldContain("dropped 2 entries", customMessage: "the old-format entry and the wrong-tag entry, and nothing current");
        cached.ShouldBe(0, "every entry the view asked for held the wrong bytes");
        landed.ShouldBe(wanted, "a refused entry is fetched from the network, never left as a hole");
        // Up to the first batch's own line, so a later batch's refusals cannot be counted against it.
        var firstBatch = console.TakeWhile(line => line != first).ToArray();
        Lines(firstBatch, "from the cache refused").ShouldBe(wanted, "each refused entry says so");
        Lines(console, "is not the one the manifest names").ShouldBe(0);
        Lines(console, "bytes do not fit the offset table").ShouldBe(0);

        // The members that landed are cached under their tags; wait until every one is written, so the
        // reload cannot interrupt a write and read as a miss.
        var held = await page.EvaluateAsync<int>("""
            async ({ version, tags, wanted }) => {
                for (;;) {
                    const db = await new Promise((resolve, reject) => {
                        const req = indexedDB.open("tianwen-atlas", 1);
                        req.onsuccess = () => resolve(req.result);
                        req.onerror = () => reject(req.error);
                    });
                    const sizes = await Promise.all(tags.map((tag, m) => new Promise(resolve => {
                        const req = db.transaction("tyc2", "readonly").objectStore("tyc2").get(version + ":m" + m + ":" + tag);
                        req.onsuccess = () => resolve(req.result ? req.result.byteLength : 0);
                        req.onerror = () => resolve(0);
                    })));
                    db.close();
                    const good = sizes.filter(size => size > 10).length;
                    if (good >= wanted) return good;
                    await new Promise(resolve => setTimeout(resolve, 200));
                }
            }
            """, new { version = CacheVersion, tags, wanted = landed });
        output.WriteLine($"[cache] {held} members held with their bytes before the reload");

        console.Clear();
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var second = await WaitForLineAsync(console, line => MembersLine().IsMatch(line), ct);
        output.WriteLine(second);
        var (landedAgain, wantedAgain, cachedAgain) = Parse(second);

        landedAgain.ShouldBe(wantedAgain);
        cachedAgain.ShouldBe(wantedAgain, "the same view on the next visit reads every member from the cache");
        Lines(console, "from the cache refused").ShouldBe(0);
    }

    /// <summary>Each member's tag, as the app makes it: its CRC32 from the manifest, eight hex digits.</summary>
    /// <remarks>Layout as <c>Tycho2MemberManifest.Write</c> writes it, version 2: magic, version, member count,
    /// region count, raw length; the region boundaries (count + 1); one CRC32 per member.</remarks>
    private static string[] Tags(byte[] manifest)
    {
        BinaryPrimitives.ReadInt32LittleEndian(manifest.AsSpan(4)).ShouldBe(2, "a version 2 manifest names each member's content");
        var count = BinaryPrimitives.ReadInt32LittleEndian(manifest.AsSpan(8));
        var crcStart = 20 + ((count + 1) * 4);
        return [.. Enumerable.Range(0, count).Select(m =>
            BinaryPrimitives.ReadUInt32LittleEndian(manifest.AsSpan(crcStart + (m * 4))).ToString("x8", System.Globalization.CultureInfo.InvariantCulture))];
    }

    private static (int Landed, int Wanted, int Cached) Parse(string line)
    {
        var match = MembersLine().Match(line);
        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
    }

    private static int Lines(IEnumerable<string> console, string marker)
        => console.Count(line => line.Contains(marker, StringComparison.Ordinal));

    // Bounded by the test's own timeout, never a clock of its own (CLAUDE.md, "Test Collections & Parallelism").
    private static async Task<string> WaitForLineAsync(ConcurrentQueue<string> console, Func<string, bool> match, CancellationToken ct)
    {
        while (true)
        {
            if (console.FirstOrDefault(match) is { } line)
            {
                return line;
            }
            await Task.Delay(100, ct);
        }
    }
}
