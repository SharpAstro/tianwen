# bake-comets

Bakes the two comet assets the browser build serves same-origin, because **JPL sends no CORS headers
from either host it serves comets from**:

| Asset | Source | Consumed by |
|---|---|---|
| `comets-sbdb.json` | SBDB query API, mirrored **verbatim** | `SbdbCometSource`, unmodified |
| `comets-apparitions.json` | Horizons, per object, resolved here | `ApparitionSeedSource` → `CometRepository` |

```bash
dotnet run --project tools/bake-comets/BakeComets.csproj -- \
  --out src/TianWen.UI.Web/wwwroot \
  [--seed <url|path>] [--max-fetches 200] [--delay-ms 250]
```

Both outputs are gitignored; `pages.yml` runs this before publish so they ride the static-web-assets
pipeline. For a local dev session with comets, run it with just `--out`.

## Why a tool and not curl + jq

The step this replaced curled SBDB and shape-checked it with `jq`. That cannot do the second asset:
deciding *which* comets need refining is `CometElements.IsElementSetStale`, and reading Horizons' reply
is `HorizonsCometSource`'s parser. Running our own code also means the SBDB query string comes from
`SbdbCometSource.DefaultQueryUrl` rather than being restated in YAML under a "keep them in sync"
comment.

## Why the overlay exists at all

Only a *successful* Horizons fetch was ever recorded, and the single-flight key cleared in a `finally`,
so a host that could never reach Horizons retried forever. Measured on the deployed site: **45 requests
and 50 s of cumulative request time in one four-minute session, all for comet 5D/Brorsen**. The overlay
is written with `NoRemoteRefresh`, which switches the per-object fetch off rather than merely feeding it.

`CometRepository` also gained a per-comet retry cooldown, which is the real fix — a dev server without
these assets, an offline desktop and a JPL outage all take that same path.

## Incremental, seeded from the live site

`--seed` is normally the **currently deployed** overlay, which makes the published site its own
incremental state: nothing to keep in sync with what is live, and no CI cache to be evicted between
deploys that may be weeks apart. Entries carry their own `FetchedUtc` and are re-resolved on a per-comet
TTL tiered by peak-ish magnitude `M1 + K1*log10(q)` — the planner's own candidacy gate rather than a
threshold invented here:

| Peak magnitude | TTL | Comets |
|---|---|---:|
| ≤ 12 | daily | 16 |
| ≤ 15 | weekly | 63 |
| ≤ 18 | monthly | 209 |
| fainter, or no M1/K1 | fetch once | 230 |
| | | **518 stale of 4,071** |

≈30 requests on a day that deploys. **The tiers are a relevance budget, not physics** — osculating
elements decay with time, so a magnitude-19 comet's set goes stale exactly as fast as 45P's; brightness
only sets how much along-track error is worth a request.

A cold start (first run, or an unreachable seed) is bounded by `--max-fetches` and reports what it
deferred; the next run picks those up, because a missing entry is simply a miss.
