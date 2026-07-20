using System.Diagnostics;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Shared setup for the web browser suite: makes sure a TianWen.UI.Web dev server is reachable and
/// launches one headless browser that every test opens a fresh, isolated context on. Mirrors
/// chess's ChessWebFixture.
///
/// Server: set <c>TIANWEN_WEB_BASEURL</c> to reuse a dev server you already have running (fast local
/// iteration — the interpreted WASM cold-boot is slow, so reusing a warm server matters here);
/// otherwise the fixture starts <c>dotnet run --project TianWen.UI.Web</c> itself and tears it down
/// at the end (the self-contained path CI would use).
///
/// Browser: bundled Chromium by default; set <c>TIANWEN_E2E_CHANNEL</c> (e.g. <c>msedge</c>) to
/// drive a system-installed browser instead — the reliable path on win-arm64, where the native Edge
/// avoids the bundled-Chromium download question (this is a win-arm64 dev box).
/// </summary>
public sealed class TianWenWebFixture : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private Process? _server;

    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl { get; private set; } = null!;

    // Shared "warm" page: booted ONCE (interpreted-WASM cold boot + catalog load ~50s) and reused by
    // interaction tests that reach their state via IN-APP navigation (chip clicks / gestures), which do
    // NOT reload -> no re-boot. The collection runs sequentially, so access is single-threaded; each warm
    // test arranges its own start state (active view, dismiss any stray overlay) to stay order-independent.
    // Closed with its context at fixture teardown. Load-behaviour tests (first-load / URL-parse asserts)
    // still take a fresh NewPageAsync() per test.
    private IPage? _warmPage;
    private IBrowserContext? _warmContext;
    private const float ReadyTimeoutMs = 120_000;

    public async ValueTask InitializeAsync()
    {
        BaseUrl = (Environment.GetEnvironmentVariable("TIANWEN_WEB_BASEURL") ?? await StartServerAsync())
            .TrimEnd('/') + "/";

        var channel = Environment.GetEnvironmentVariable("TIANWEN_E2E_CHANNEL");
        EnsureBrowserInstalled(channel);

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Channel = string.IsNullOrWhiteSpace(channel) ? null : channel,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_warmContext is not null) await _warmContext.CloseAsync();
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();

        if (_server is { HasExited: false })
        {
            try { _server.Kill(entireProcessTree: true); }
            catch { /* already gone — nothing to clean up */ }
            _server.Dispose();
        }
    }

    /// <summary>
    /// The shared warm page, booted once (see the <c>_warmPage</c> field). Use for interaction tests
    /// that reach their state via in-app navigation; do NOT use for tests that assert first-load /
    /// URL-parse behaviour (those need a fresh <see cref="NewPageAsync"/> with a specific initial URL).
    /// Loaded with <c>?e2e=1</c> so the read-only view-state / rect hooks are registered (they persist
    /// across in-app navs regardless of later query strings).
    /// </summary>
    public async Task<IPage> WarmPageAsync()
    {
        if (_warmPage is not null) return _warmPage;

        var page = await NewPageAsync();
        _warmContext = page.Context;
        await page.GotoAsync(BaseUrl + "?e2e=1",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=planner]")).ToBeVisibleAsync(new() { Timeout = ReadyTimeoutMs });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = ReadyTimeoutMs });
        _warmPage = page;
        return _warmPage;
    }

    /// <summary>Opens a fresh, isolated context (own storage) and a blank page.</summary>
    public async Task<IPage> NewPageAsync()
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            // Geolocation is best-effort in the app (default site on denial); grant a fixed
            // position so the tests are deterministic and never hit a permission prompt.
            Permissions = ["geolocation"],
            Geolocation = new Geolocation { Latitude = 47.5f, Longitude = 11.0f },
            // Enable touch so the canvas gesture tests can synthesize touch events over CDP
            // (the canvas touch listeners only fire when the browser reports touch support). The
            // app doesn't branch on touch capability, so the mouse/DOM tests are unaffected.
            HasTouch = true,
        });
        return await context.NewPageAsync();
    }

    // ── server lifecycle ────────────────────────────────────────────────────

    private async Task<string> StartServerAsync()
    {
        var repoRoot = FindRepoRoot();
        // A distinct port from the conventional 5099 dev server, so an auto-started run never
        // collides with one a developer already has up.
        const string url = "http://127.0.0.1:5188";

        // --no-launch-profile is load-bearing: TianWen.UI.Web's launchSettings.json pins
        // applicationUrl to :5099, and `dotnet run` applies that OVER our ASPNETCORE_URLS/--urls, so
        // without this the dev server comes up on :5099 while WaitForServerAsync polls :5188 -> a
        // guaranteed 5-minute timeout. Skipping the profile lets the explicit --urls (passed as an app
        // arg after --) win. ASPNETCORE_URLS is kept as a belt-and-suspenders fallback.
        _server = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet",
                $"run --project \"{Path.Combine(repoRoot, "src", "TianWen.UI.Web")}\" -c Release -p:Lightweight=true --no-launch-profile -- --urls {url}")
            {
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        _server.StartInfo.Environment["ASPNETCORE_URLS"] = url;

        // Drain stdout+stderr. RedirectStandard* fills a ~4 KB OS pipe buffer and then BLOCKS the child
        // until someone reads it; a WASM `dotnet run` prints far more than that during its build, so an
        // undrained pipe wedges the server before it ever listens -> the other way this used to time out.
        // Keep a bounded tail so a genuine startup failure surfaces in the exception, not a bare timeout.
        var tail = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Capture(string? line)
        {
            if (line is null) return;
            tail.Enqueue(line);
            while (tail.Count > 80) tail.TryDequeue(out _);
        }
        _server.OutputDataReceived += (_, e) => Capture(e.Data);
        _server.ErrorDataReceived += (_, e) => Capture(e.Data);
        _server.Start();
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        try
        {
            await WaitForServerAsync(url, TimeSpan.FromMinutes(5));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}\n--- last dev server output ---\n{string.Join('\n', tail)}", ex);
        }
        return url;
    }

    private static async Task WaitForServerAsync(string url, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode) return;
            }
            catch { /* not up yet */ }
            await Task.Delay(500);
        }
        throw new TimeoutException($"TianWen.UI.Web dev server did not come up at {url} within {timeout}.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "TianWen.UI.Web", "TianWen.UI.Web.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repo root (no src/TianWen.UI.Web/TianWen.UI.Web.csproj above the test binary).");
    }

    // ── browser install ─────────────────────────────────────────────────────

    // A system-channel run (msedge/chrome) uses an already-installed browser — nothing to fetch.
    // For the bundled default, drive Playwright's own installer so a clean checkout needs no manual
    // `playwright install` step before the first run.
    private static void EnsureBrowserInstalled(string? channel)
    {
        if (!string.IsNullOrWhiteSpace(channel)) return;

        var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exit != 0)
            throw new InvalidOperationException(
                $"`playwright install chromium` failed (exit {exit}). On win-arm64, set " +
                "TIANWEN_E2E_CHANNEL=msedge to use the system Edge instead.");
    }
}

[CollectionDefinition(Name)]
public sealed class TianWenWebCollection : ICollectionFixture<TianWenWebFixture>
{
    public const string Name = "tianwen-web";
}
