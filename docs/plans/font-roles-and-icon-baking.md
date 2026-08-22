# Font roles and icon baking

Raised by the user 2026-08-19, out of the FITS-viewer toolbar mark work: *"we should have one common
font resolving that supports symbols + emoji + OS fallback, the whole shebang. and we should be able to
prebake icons."*

Two asks, and they meet in the middle: the bake needs a resolver good enough to **find a licensed source
face**, and a baked icon needs no font at runtime at all.

## Read this first: most of it already exists

The reason this plan opens with an inventory is that the emoji half of it was written **twice by
accident** in a single afternoon -- once inline in `VkGuiRenderer.ResolveFontPath`, then again as
`TianWen.UI.Abstractions/EmojiFonts.cs` -- and a third, much larger duplicate (a whole fallback
resolver) was one step from being started before `DIR.Lib.FontFallbackResolver` was found. The tell each
time was searching for *the thing about to be written* rather than for *the problem it solves*.

| Already shipped | Where | What it does |
|---|---|---|
| `FontFallbackResolver.FromRoles` | DIR.Lib | primary -> symbol -> emoji -> per-script chain, **per-codepoint coverage read from each face's cmap** (`OpenTypeFont.GetGlyphId`), lazy face loading, `CanRender(Rune)`, `TryResolveFont(Rune)`, `CoverageRuns`, backend-generic `Measure`/`Draw`/`FitEllipsis` |
| `FontResolver` | DIR.Lib | `ResolveSystemFont` (monospace default), `ResolveSystemScriptFonts(extra)` (CJK/Indic/Arabic), `ResolveInstalledFace`, `EnumerateInstalledFonts` incl. per-user dirs |
| `tools/BakeShaders` | tianwen | the bake PRECEDENT: build-host-time generation, output committed AND embedded, warning `TWSH0001` when a source is newer than its baked artifact |
| `ManagedFontRasterizer` | DIR.Lib | glyph rasterisation, already the engine behind `Renderer.DrawText` |
| `IconKind` + pixel painter | DIR.Lib | 11 procedural marks built from rectangles, with a cell-surface counterpart in `CellLayout` |

`FontFallbackResolver`'s own doc already carries the reasoning a new design would have had to
rediscover: the role order is primary -> symbol -> emoji -> script **because** several script faces
incidentally carry a few symbols (the Noto CJK faces cover the caret glyphs), so without the symbol face
ahead of them a caret is drawn from a multi-megabyte CJK font.

## The actual gaps

1. **`FontResolver` has no symbol or emoji candidate tables.** It knows about monospace defaults and
   per-script families; the emoji probe was hand-rolled inline in a host, which is why it got duplicated
   rather than found. Both inline versions were **Windows-only**, so a Linux or macOS host resolved no
   emoji face at all and every emoji mark silently drew nothing.
2. **Nobody passes `symbolFontPath`.** Even the GUI, the one consumer of `FromRoles`, omits the symbol
   role -- so the ordering its doc argues for has never actually been exercised.
3. **The FITS viewer does not use the fallback resolver at all.** `ImageRendererBase.DrawText` passes one
   `FontPath` straight to `Renderer.DrawText`, so a codepoint the UI face lacks draws `.notdef`.
4. **Existence is not coverage.** `EmojiFonts.Resolve` checks that a *file* exists. Whether that face
   contains U+1F300 needs its cmap -- which `CanRender(Rune)` already answers. Today the Objects mark can
   resolve `seguiemj.ttf`, conclude it has an emoji face, and draw nothing.
5. **A mark at 13-20 design units cannot be reliably hand-drawn.** Three attempts at a spiral failed (see
   the toolbar entry in [`docs/todo/ui.md`](../todo/ui.md)); a font designer has already solved it at that
   size. But a colour emoji **cannot be tinted**, so it cannot dim on a disabled button -- the exact bug
   that had just been fixed for the Channel bars.

## Phases

| Phase | Work | Repo | Notes |
|---|---|---|---|
| **F1** | **PARTLY DONE** (DIR.Lib 8.7). `FontResolver.ResolveEmojiFont(extra)` shipped with per-OS tables; `""` on miss, matching `ResolveSystemFont`. `ResolveSymbolFont(extra)` is NOT done | DIR.Lib | Windows `seguiemj` / macOS Apple Color Emoji / Linux Noto Color Emoji all landed. The SYMBOL face (`seguisym`, Apple Symbols, DejaVu) is still outstanding, and is what would cover the non-emoji pictographs an emoji font lacks. Bundled assets arrive via `extra`, so DIR.Lib needs no knowledge of a caller's layout |
| **F2** | **PARTLY DONE**. `EmojiFonts.cs` is gone; `BundledFonts.Resolve()` is the ONE entry point and all three hosts (GUI chrome, viewer, TUI) call it. `symbolFontPath:` is not passed, pending the F1 remainder | tianwen | The consolidation went further than planned: the platform tables moved to F1, the roles resolve together so no caller can resolve a subset, and the result is cached per process -- which is what makes a per-widget resolve affordable at all |
| **F3** | **PARTLY DONE**. The viewer now HAS a chain (built by `BundledFonts.Resolve`, adopted only when its primary matches the face in use) and `DrawText` routes through it via `PixelWidgetBase.FontFallback`. Marks still gate on file existence, NOT `CanRender(Rune)` | tianwen | The remaining half is the behavioural payoff and is newly possible: before the chain existed here there was nothing to ask. This is what makes the mark fallback correct rather than lucky |
| **B1** | **DONE** (DIR.Lib 8.5). `IconBaker` (glyph -> coverage runs) + `PixelWidgetBase.DrawCoverageMask`, and the `DIR.Lib.IconBaker` tool over them | DIR.Lib | Landed UPSTREAM, not as `tools/BakeIcons`: see the note below. `dnx DIR.Lib.IconBaker` |
| **B2** | **DONE**. The Objects mark is the baked `Spiral` (U+1F300); the emoji draw and the hand-drawn spiral are both gone | tianwen | Baked icons are single-channel, so they tint and dim like any other ink |
| **B3** | Consider upstreaming the monochrome, stateless marks as `IconKind` members | DIR.Lib | Only if a cell surface can say them; see the open question below |

F1-F3 and B1-B3 are independent. **Nothing else is blocked on either**: the toolbar marks shipped
without them, using the interim resolver plus a geometric fallback.

### Where the bake actually landed, and why not here

B1 was planned as `tools/BakeIcons` in this repo, mirroring `tools/BakeShaders`. It shipped in **DIR.Lib**
instead, split in two, and the split is the part worth keeping straight:

- **`IconBaker` is a library API**, not a build step, because a build-time bake cannot serve every case: a
  theme that turns the whole UI one colour (Night) wants its normally-full-colour emoji as tintable
  coverage, and which emoji those are is not known until the app draws them. Runtime callers cache per
  (codepoint, size).
- **`DIR.Lib.IconBaker` is the tool**, consumed via `dnx DIR.Lib.IconBaker`. It owns only the generated-file
  format and the argument parsing. It ships from the library's repo because nothing in it is
  app-specific -- which glyphs, at which sizes, into which namespace are all arguments -- so leaving it in
  the first app that needed it would have produced a vendored copy the moment a second app wanted icons.

The generated data stays app-local (`src/TianWen.UI.Abstractions/BakedIcons.g.cs`, committed), which also
answers the plan's open question below for the DATA even though B3 (upstreaming the marks as `IconKind`)
is still open.

**The `TWSH0001`-style staleness warning was NOT built, deliberately.** A timestamp check is the weaker
tool here: `ManagedFontRasterizer` is pure managed, so a re-bake is byte-reproducible on any host (verified
by re-baking `BakedIcons.g.cs` with the moved tool and diffing -- identical). That admits a CI step that
**re-bakes and compares**, which catches a hand-edited or half-committed table, where a timestamp only
catches a *forgotten* re-bake. Still outstanding.

## Traps

- **Licensing gates the bake.** Baking glyph *shapes* redistributes them. Noto is OFL -- fine, and
  already bundled by the GUI. **`seguiemj.ttf` is Microsoft-proprietary and must never be a bake
  source**, even though it is what the runtime probe legitimately falls back to on Windows. Runtime use
  of an installed system font and committing its outlines are different acts.
- **Rects, not a raster.** A tinted-bitmap primitive would need a new member on the abstract renderer
  seam; rect runs draw through the `FillRect` that is already there, which is also how DIR.Lib's own
  `IconKind` painter works. A 16x16 mask is typically 10-40 rects.
- **Bake resolution is a decision, not a detail.** Rects are a fixed grid, so either bake the DPI steps
  that matter (1x / 1.5x / 2x -> 13 / 20 / 26 px) or bake one oversampled mask and snap. Baking an SDF
  instead is tempting until you notice it re-invents what the font already gave you.
- **Judge a mark at its real pixel size, and get the real pixels.** The `sdl-ui-inspector` screenshot is
  DOWNSCALED (a 2902 px framebuffer arrived as ~1999, a factor of 0.69), which destroys exactly the
  sub-pixel detail an icon is made of -- a correctly tapered star arrived as a plain `+` and was judged
  broken. Use a DPI-aware `PrintWindow` capture (`SetThreadDpiAwarenessContext(-4)`, or PowerShell
  virtualises the coordinates and downscales too) plus nearest-neighbour magnification.
- **A colour mark dims by losing brightness, not by taking grey ink**, because its hue is the
  information. Anything baked is monochrome and sidesteps this entirely -- which is half the point.
- **Not every emoji is bakeable, and the ones that fail fail identically: as a solid blob.** The bake
  takes the glyph's ALPHA silhouette, so structure carried by COLOUR contrast is discarded while
  structure carried by GAPS survives. Measured at 20 px against the bundled Noto: `Sparkles` U+2728 and
  `Magnifier` U+1F50D read clearly (distinct forms separated by transparency, and the magnifier's lens
  interior sits at lower alpha than its rim); `FolderOpen` U+1F4C2, `Crosshair` U+1F3AF and `DoubleUp`
  U+23EB come out as **fully inked rectangles** -- a folder's flap, a target's rings and a double
  triangle are all drawn as colour against colour, with no hole anywhere. The spiral works for exactly
  the complementary reason: its arms are separated by gaps, not by hue.
  **So test a candidate by rendering the MASK, never by looking at the emoji.** In a text editor or a
  colour preview every one of the failures above looks like a perfectly good icon. Bake it at 20 px and
  print the runs as ASCII (a dozen lines of script over the generated table); a blob is unmistakable and
  costs one command to find. This is the same error as judging a mark from the downscaled inspector
  screenshot, one level down: the artifact being looked at is not the artifact that ships.
  Corollary: a mark whose meaning needs an outline (Open, A/B compare, Boost) is better PROCEDURAL than
  baked, which is what B3 already assumed for a different reason.

## Open question

**Where does baked output live?** App-local in tianwen is simpler and has the only consumer today. DIR.Lib
`IconKind` is the shared home, but its contract is that a kind "earns its place by having a consumer on
both surfaces", and a 40-rect emoji-derived mask means nothing to a terminal -- `CellLayout` would pick a
glyph instead. So B3 is likely only for marks that BOTH surfaces can say, which the spiral is probably
not. Decide before generating anything, since it changes where the generator writes.

---

## Which face a widget gets is ONE decision, in one place (moved out of CLAUDE.md, 2026-08-22)

`BundledFonts.Resolve()` returns `(Text, Emoji, Fallback)` **together** -- app policy is "prefer the
file we ship, per role", and DIR.Lib's `FontResolver` owns every platform role behind it
(`ResolveSystemFont` / `ResolveSystemScriptFonts` / `ResolveEmojiFont`, the last added in 8.7). All
three hosts call it: `VkGuiRenderer`, `ImageRendererBase`, `TuiFontPath`. **A direct `FontResolver.`
call in production code is a regression** (tests are exempt and should keep using it -- a layout test
wants a deterministic always-present face, not the bundled-first policy). Four things this shape is
load-bearing for:

- **Resolving a SUBSET is the bug it prevents.** Both UI hosts used to resolve the roles themselves
  in the same order, and only the GUI chrome went on to build the coverage chain -- so the viewer had
  faces but no `FontFallback`, could not ask `CanRender`, and therefore gated marks on file
  existence. Every missing glyph was then found visually, per glyph, by a human looking at the
  toolbar (the blank plate-solve tick and the flat-topped globe both).
- **The per-process `Lazy<FontSet>` cache is not an optimisation.** `ResolveSystemScriptFonts` looks
  up ~14 family NAMES, i.e. enumerates installed fonts. One chrome object resolves for every GUI tab,
  but the viewer is constructed several times over (preview, guide-cam, planetary), so an uncached
  shared entry point would multiply that by widget count.
- **Adopt a shared chain only when its primary IS the face in use.** `ImageRendererBase` checks
  `FontPath == chain.PrimaryFontPath`. A host that pushed its own text face would otherwise get
  coverage answers about a different primary: the chain calls a rune drawable because the bundled
  face carries it, while the widget draws with the pushed one and shows nothing.
- **Bundled first, because a bundled face is the only one whose COVERAGE is known.** A system face
  that lacks a codepoint draws NOTHING, which is indistinguishable from a broken control -- the
  Windows monospace default is Consolas, which carries no check mark. Falling back to the platform is
  still right: a host that bundles nothing must draw something.

Still outstanding (F1-F3 above, PARTLY DONE): no `ResolveSymbolFont` yet, so `symbolFontPath:` is not
passed; and marks still gate on file existence rather than `CanRender(Rune)`.
