# TianWen.UI.Web.E2E

Browser end-to-end tests for **`TianWen.UI.Web`** (the in-browser Planner + Sky Atlas), driven with
[Playwright for .NET](https://playwright.dev/dotnet/) + xUnit v3. Mirrors chess's `Chess.Web.E2E`.

The planner list and sky map are drawn into a `<canvas>`, so the tests deliberately assert only on
the observable **chrome DOM** that the unit tests can't reach:

- the titlebar **view chips** (`[data-view=planner]` / `[data-view=sky]` and their `active` class),
- the **address bar** (there is no Blazor Router — the chips navigate via `NavigationManager` and the
  component parses the path itself; deep links `/planner`, `/sky-atlas`, aliases `skymap`/`sky`),
- the document **title** (`<PageTitle>` → `Astro - Planner` / `Astro - Sky Atlas`),
- the aria-live **status line** (`.status`),
- and the **catalog-loading indicator** (`.catalog-loading`, shown beside the chips while the shared
  catalog loads — the chips never block).

This suite exists because the same navigation bug shipped three times in one session (chip clicks
changing the URL but not the active view; the chip blocked during the catalog load). Each test fails
on that regression.

## Why it lives outside `TianWen.slnx`

Exactly like `TianWen.UI.Web` itself: this project needs a browser and a running dev server, so it
must **not** be picked up by the solution-wide `dotnet test` that CI runs on every push. It also opts
out of the repo's Central Package Management (carries its Playwright/xUnit versions inline). Run it
explicitly.

## Running

```bash
# 1. bring up the app (leave running in another terminal). Interpreted WASM cold-boot + catalog
#    load is slow (~50 s), so reusing a warm server across tests matters.
dotnet run --project src/TianWen.UI.Web -c Release -p:Lightweight=true   # serves http://localhost:5099

# 2. point the tests at it and run. On win-arm64 use the system Edge (no bundled-Chromium download).
TIANWEN_WEB_BASEURL=http://localhost:5099 TIANWEN_E2E_CHANNEL=msedge dotnet test src/TianWen.UI.Web.E2E
```

If `TIANWEN_WEB_BASEURL` is **not** set, the fixture starts its own `dotnet run` on port 5188 and
tears it down afterwards (the self-contained path).

### Browser

- **Default:** bundled Chromium. The fixture runs `playwright install chromium` on first use, so a
  clean checkout needs no manual install step.
- **win-arm64:** set `TIANWEN_E2E_CHANNEL=msedge` to drive the natively-installed Edge instead and
  skip the bundled-Chromium download entirely.
