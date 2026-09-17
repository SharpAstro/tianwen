# Object imagery from Wikipedia and Wikimedia Commons (plan)

**Status: P0 and P1 DONE (2026-09-17 and 2026-09-18); P2 NOT STARTED. Written 2026-09-17** from the astrophoto.app field note in
[inbox.md](../todo/inbox.md) ("Field note: astrophoto.app"), after reading that site's shipped JavaScript
and measuring every endpoint below. Raised by the user: the viewer and the atlas should show an object's
picture the way that site does, with attribution, positioned on the sky where we can, and the identity
of "which article is this object" decided once at bake time instead of guessed at click time.

Companions: [in-app-sky-atlas](in-app-sky-atlas.md) (the selection panel this feeds),
[web-showcase](web-showcase.md) (the browser build), [skymap-milkyway](skymap-milkyway.md) (the other
imagery layer, and its browser phase, which lands the texture seam this plan reuses).

## What existed before P0

- `PlannerDetails.GetWikipediaUrl` built `https://en.wikipedia.org/wiki/` + the object's MAIN catalogue
  designation (`CatalogIndex.ToCanonical()`, spaces to `_`). It was a GUESS: the planner's name line linked
  there and nothing checked the page existed or was about this object. `PlannerDetailsTests` pinned only
  the URL shape. Since P0 it reads the verified table, and an unverified object gets no link.
- Nothing fetched a summary, an image, or a licence: the atlas info panel and the viewer's selection
  panel (`ObjectInfoPanel`) showed catalogue data only. Since P1 they show the article's picture and its
  credit; a summary extract is still not fetched.

## What astrophoto.app actually does (read from its bundle, 2026-09-17)

- A **hand-written list of 30 targets**, each with a Wikipedia title typed beside it
  (`{name:"Sculptor Galaxy", designation:"NGC 253", ..., wiki:"Sculptor_Galaxy"}`). It does not solve the
  identity problem; it avoids it.
- One call per target from the browser: `en.wikipedia.org/api/rest_v1/page/summary/{title}`, taking
  `thumbnail.source` for the card, `originalimage.source` for the click-through, `extract` for the text.
- **No credit anywhere.** The bundle has no licence, artist or attribution handling at all, and the
  click-through shows a JWST image full-screen with Wikipedia's extract and nothing else.

## Measurements

### The browser can fetch all of it

`Access-Control-Allow-Origin: *` on all four endpoints, checked with a browser `Origin` header:

| Endpoint | Used for |
|---|---|
| `en.wikipedia.org/api/rest_v1/page/summary/{title}` | title, `wikibase_item`, lead image URLs, extract |
| `commons.wikimedia.org/w/api.php?action=query&prop=imageinfo&iiprop=extmetadata\|url\|size&origin=*` | licence, artist, credit, standard-width thumbnail URL |
| `upload.wikimedia.org/...` (image bytes) | the picture |
| `query.wikidata.org/sparql` | catalogue code to item |

Two constraints that bite:

- **The image server only serves standard widths.** A `640px-` thumbnail was refused with
  `400 Use thumbnail sizes listed on https://w.wiki/GHai`; the summary's own URLs were 330 and 3840 px, and
  `iiurlwidth=1024` came back as a 1280 px URL. Record the FILE NAME at bake time and ask for a standard
  bucket at runtime; never build a width into a URL by hand.
- A browser cannot set `User-Agent`, which Wikimedia's policy asks clients to identify with. Send
  `Api-User-Agent` instead; the CORS preflight allows it (`access-control-allow-headers` lists it).

### The licence is not in the summary

The summary carries no licence. Commons `extmetadata` does, per file. For `Lagoon_Nebula_(ESO).jpg`:
`LicenseShortName = CC BY 4.0`, `AttributionRequired = true`, `Artist = ESO/S. Guisard`, plus a `Credit`
HTML fragment. So a credit line is TWO calls per object at runtime, or zero if the bake stores it.

### Today's title guess is right more often than feared, and fails silently when it is not

30 designations in `ToCanonical()` spelling against the summary endpoint (redirects followed):

| Outcome | Count | Examples |
|---|---|---|
| Resolved to the right object | 25 | `Sh2-25` to Lagoon Nebula, `HR 1713` to Rigel, `PGC 2557` to Andromeda Galaxy, `Cr 399` to Brocchi's Cluster |
| 404, a dead link today | 5 | `ACO 1656`, `HIP 27913`, `vdB 142`, `GUM 12`, `Ced 214` |

24 of the 25 had a lead image (`HD 209458` has none). The sample was hand-picked across catalogues, not
drawn from the database, so it bounds nothing; it shows the failure is a dead link rather than a wrong
page for these catalogues.

### Wikidata finds what the guess misses, and misses what the guess finds

Exact `P528` (catalog code) lookup of 18 spellings:

| Found by Wikidata, missed by the guess | Missed by Wikidata, found by the guess | Wrong without the catalogue qualifier |
|---|---|---|
| `HIP 27913` Chi1 Orionis, `ACO 1656` Coma Cluster, `Gum 12` Gum Nebula | `Sh2-25`, `Cr 399`, `Mel 111`, `vdB 142` (no item carries those codes) | `B 33` matched a music bibliography and a Brown catalog entry |

`Ced 214` has an item and an image but no English article. `LDN 1622` has an article whose lead image
Wikidata does not list (`P18` empty). **Neither source is enough alone.**

### Some images already know where they are on the sky

Lagoon Nebula's actual lead image, `VST_images_the_Lagoon_Nebula.jpg`, carries AVM (Astronomy
Visualization Metadata) in its XMP: `Spatial.ReferenceValue 270.904114 -24.386937`, `Spatial.Scale`
5.92e-5 deg/px (0.21 arcsec/px), plus rotation, reference pixel and coordinate frame. That is a WCS with
no plate solve. The ESO `Lagoon_Nebula_(ESO).jpg` has none. Space-agency images (ESO, ESA/Hubble, NASA)
are the ones likely to carry it; coverage is unmeasured.

### P0 measured: what verifies against our catalogue (2026-09-17)

**Scope, as the user agreed it: objects with a common name, plus Messier and Caldwell.** Taken from the
object DB that is 11,019 indices, and the number hides a split: **10,383 are stars whose "common name" is a
Bayer or Flamsteed designation** (`32 Oph`, `gam Pic`), with 636 non-stars (627 once the planets, which
have no fixed position, are dropped). Measured on all 627 non-stars and a random 500 of the stars.

**Two routes, because each misses what the other finds:**

- **Catalogue codes** (`P528`) in Wikidata's spelling, which is not ours and not even consistent:
  `M 42` but `M99`, `SH 2-25` for our `Sh2-25`, `Gum 33` for `GUM 33`. So both Messier spellings are
  asked. The `P972` catalogue qualifier is **not usable as a check**: Caldwell codes carry cluster
  designations like `C 0021-723`, and `C 99` answers a Chopin mazurka. The position check replaces it.
- **English Wikipedia titles** (`Messier N`, `NGC N`, the common names), one `action=query` per 50 titles
  with `redirects=1` and `ppprop=wikibase_item|disambiguation`. It is what finds the items that carry no
  codes at all: M100, M110, the Hyades and the Coalsack had none.

**Verification is a separation, with a tolerance by kind.** The object's catalogued major axis where we
have one, else **one degree for an extended kind** (nebulae, clusters, associations, remnants) **and ten
arcminutes for everything else**. A two-arcminute floor rejected correct matches for every large object our
catalogue gives no size: the Pleiades sat 6.6' off, North America 13', the Witch Head 58'. Wrong code
matches land thousands of arcminutes away (the p90 separation over all candidates was 2,044').

| Group | Objects | Any candidate | Position verified | With an English article | With an image |
|---|---|---|---|---|---|
| Non-stars | 627 | 611 | 603 | 527 | 499 |
| of which Messier | 107 | 107 | 107 | 107 | 107 |
| of which Caldwell | 108 | 107 | 106 | 104 | 106 |
| Stars (500 sampled, codes only) | 500 | 499 | 498 | 353 | 94 (Wikidata `P18`) |

- **Choosing between verified items:** 8 non-stars verify against more than one item with an article
  (Carina Nebula and Keyhole Nebula; NGC 2070 and Tarantula Nebula; IC 434 and Flame Nebula). Rank an item
  found by both routes over code only over title only, then by separation. That picks Carina, NGC 2070 and
  IC 434, each the article about the index itself. Title-only accepts, reviewed by hand: all right or a
  deliberate Wikipedia redirect (`Maia Nebula` to the star Maia, `Burnham's Nebula` to T Tauri).
- **One object, many indices.** The 527 indices resolve to 309 distinct articles: our catalogue lists the
  Carina Nebula as NGC 3372, C92, GUM 33 and RCW 53. The table maps index to item and stores each item once.
- **Use the article's lead image, not Wikidata's `P18`.** They agree on 98 of 309 items; the lead image is
  what the article shows and exists for 305. **Drop SVG leads**: all 17 are constellation maps.
- **Every lead image is on Commons** (298 files, none local or fair use), so every one has `extmetadata`:
  84 CC BY 4.0, 59 public domain, 43 CC BY-SA 4.0, 39 CC BY-SA 3.0, 30 CC0, 20 CC BY 2.0, 17 CC BY 3.0,
  5 other CC BY-SA, 1 with no licence field. 208 require attribution. Median width 2,405 px, p10 667 px.

### The full bake, and what star articles lead with (2026-09-17)

The whole scope through the tool, stars included by the user's decision:

| | Indices in scope | With a candidate | With a verified article |
|---|---|---|---|
| Non-stars | 627 | 611 | 527 |
| Stars | 10,382 | 10,373 | 7,446 |

7,973 indices resolve to **2,785 articles** (a star is three indices, HR, HD and HIP), 81 KB gzipped.
**All 110 Messier objects** have one; Caldwell misses the same four the sample did (C33 Eastern Veil,
C40, C41 Hyades, C74 Eight-Burst Nebula).

**The images are where stars differ.** 2,311 articles have a lead image but they share only 581 files,
because a star article usually leads with a picture that is not of the star:

- **156 SVG files are constellation maps**, shared across roughly 1,800 star articles.
- **105 more are charts**: light curves (`54AurLightCurve.png`), position charts, star maps, asterism
  diagrams. Commons' own categories flag 100 of them ("Light curves of Delta Scuti variables", "Star
  location maps") and the file name 103, with 98 in common, so either one drops a file.
- **1 file has no licence field**, and no credit line could state one.

That leaves **337 articles with a picture**, almost all of the non-stars plus the stars that genuinely lead
with a photograph (Mintaka, Gamma Velorum). Licences: CC BY 4.0 96, public domain 81, CC BY-SA 4.0 57,
CC0 31, CC BY-SA 3.0 28, CC BY 2.0 21, CC BY 3.0 17, 6 other. A shared file that survives the filter is a
real picture shown on a member's article (the Pleiades on Alcyone, the Trapezium on Theta1 Orionis C),
which is kept: it is what the article shows.

## Design

### P0: the identity bake (the part that replaces guessing)

A build-time tool, `tools/bake-object-imagery`, run on demand and committed (not per deploy: Wikipedia
changes slowly and a bake that reruns every push would churn a table nobody reviewed). For every catalogue
object worth a picture:

1. Try **every designation we already hold for it**: the main index and its cross-indices
   (`M 8`, `NGC 6523`, `Sh2-25` are one object to us).
2. Candidates from both sources: Wikidata `P528` in Wikidata's spellings, and English Wikipedia titles
   (redirects followed, disambiguation pages dropped). Not the `P972` qualifier; see the measurements.
3. **Accept a candidate only when its sky position agrees with ours**, from Wikidata's right ascension
   (`P6257`) and declination (`P6258`), within the tolerance by kind measured above. This is what makes
   the answer a verification instead of a guess. Rank several survivors by route, then separation.
4. Record per item: Wikidata item, English article title, the article's lead image file name (not SVG),
   its licence short name, artist, credit text, `AttributionRequired`, and the image's pixel size.

Output: a compact embedded table in `TianWen.Lib` keyed by `CatalogIndex`, same shape and loading path
as the other baked catalogue snapshots. No pixels. `GetWikipediaUrl` reads the table and stops guessing;
an object absent from the table gets no link rather than a dead one.

**As built:**

- `object_articles.gs.gz`, the `.gs.gz` ASCII-record format the other catalogues use, one record per
  ARTICLE naming every index that resolved to it (raw `CatalogIndex` values, as the snapshots store
  them). Read and written by `ObjectArticleTable`; a separator byte in upstream text becomes a space.
  Named in `ILLink.Substitutions.xml`, so the thumbnail DLL drops it with the other catalogues.
- `ICelestialObjectDB.TryGetArticle(index, out ObjectArticle)` answers directly or through the index's
  cross-indices (the bake keys main entries; `M 42` reaches `NGC 1976`'s article). The table decodes in its
  own init task alongside the other phases and is joined as `object-articles-join`; the interface default
  answers false, for a host with none. Measured on arm64 Release: the decode takes 4 to 5 ms warm (16 ms
  cold, first run) for 7,973 keys, and the join waits 0.00 ms, the decode having finished long before init
  reaches it.
- `ObjectArticle` (Wikidata item, title, `Url`) and `ObjectArticleImage` (file name, licence, artist,
  credit, attribution flag, size, `FilePageUrl`) are the public shape P1 consumes.
- `tools/bake-object-imagery` is run by hand and its output committed. The table is binary, so the bake's
  stdout report is the review: coverage, Messier and Caldwell misses, licences, and every index added,
  removed or retitled since the table it replaced.

### P1: the picture in the selection panel

- The atlas info panel and the viewer's selection panel (`ObjectInfoPanel`) show the thumbnail, and a
  click opens the large image, which is the astrophoto.app interaction the user pointed at.
- **A credit line under every image**, built from the bake: "ESO/S. Guisard, CC BY 4.0", linking to the
  Commons file page. **No separate session list in the help menu** (the user's call, 2026-09-18): the
  credit travels WITH the picture, on the panel and on the large view, which is what CC BY and CC BY-SA
  ask for. A list elsewhere would be a second copy of the same fact, in the one place nobody looking at
  the picture is looking.
- Fetched on selection, never pre-fetched. Desktop caches under a new `AppData/TianWen/ObjectImages/`
  (add it to the CLAUDE.md AppData list and to whatever owns the others); the browser relies on its HTTP
  cache.
- Offline or blocked: the panel shows what it shows today. No retry storm: remember a failed file for the
  session, the lesson `ApparitionRetryCooldown` was written for.

**As built (2026-09-18), thumbnail and credit:**

- **The picture is a section of the shared panel**, under the rows and above the buttons, sized from the
  aspect ratio the table records so the panel is its final height before any byte of the picture arrives.
  At the top the close affordance would sit on the picture. `ObjectInfoPanel.BuildPictureSection` emits a
  keyed `Fill` the host draws into, plus the credit as a `LinkHit` to the Commons file page, which is a
  real anchor on the web and opens the browser on the desktop.
- **The width asked for is the slot's own pixel width**, rounded up to a standard Wikimedia width, so a
  high-DPI panel gets a sharper picture. The panel is 332 design units wide, which is the 500 px bucket at
  1.5x.
- **Desktop**: `ObjectPictureStore` (`AppData/TianWen/ObjectImages`) fetches once per width, writes the
  file atomically and decodes PNG or JPEG to RGBA; `VkObjectPictures` uploads the texture on a later frame.
  **Browser**: the same URL goes to `WebGlRenderer.LoadTextureAsync`, so the browser fetches, caches and
  decodes it natively; `WebGlObjectPictures` draws a textured quad in its own pipeline.
- **`ObjectPictureCache` is the shared policy**: one load for a panel that asks every frame, a failure left
  alone for five minutes, six pictures kept. Unit-tested on a manual clock with no GPU.
- **Sizes, measured over the baked set** (40 files sampled): a 500 px thumbnail is 61 KB at the median and
  112 KB at the mean, so all 319 files would be about 35 MB; at 1280 px it is 372 KB and 726 KB, about
  230 MB for every file. The cache only grows with what was looked at, so no cap is set yet.
- **Verified**: in the GUI atlas (the Hubble M42 mosaic, its credit under it, one 50 KB file cached) and in
  the browser atlas through the opt-in `ObjectPictureProbe`. NOT yet verified in `tianwen-fits`, which
  draws through the same Vulkan helper.
- **A click on the thumbnail opens the picture large** over the host's content, dimmed behind a scrim that
  dismisses it, with the credit under it and Escape as the way out (Escape retires the picture before the
  selection it belongs to). The same host hook fills the big slot, so it asks Wikimedia for a wider standard
  width by virtue of being bigger, and the desktop caches that width beside the thumbnail.
- **Nothing is outstanding in P1.** The session credit list was dropped rather than built, because the
  credit is already under the picture in both places it is shown.

### P2: positioned images on the atlas

- At bake time, read AVM `Spatial.*` tags where present and store the WCS.
- Where absent, plate-solve at bake time with `CatalogPlateSolver`. It wants a pixel-scale estimate; derive
  one from the object's catalogued extent against the image size, and sweep a few. **Narrow Hubble and JWST
  frames will not solve against Tycho-2** (the JWST Ring Nebula on the article has almost no catalogue
  stars in it), so those stay panel-only, which is the split the user proposed.
- Draw a positioned image on the atlas under the line work, faded with FOV like the Milky Way, through the
  same texture seam the browser Milky Way phase adds to WebGl.Renderer, and a Vulkan texture on desktop.
- Open question for the user before P2 ships: reprojecting a CC BY-SA image onto the sky may count as an
  adaptation. The safe form is to say "modified" in the credit line.

### Phasing

| Phase | Scope | Depends on |
|---|---|---|
| P0 | Identity bake + verified links | nothing |
| P1 | Thumbnail, large view, credit line, cache | P0 |
| P2 | AVM WCS + bake-time solve + atlas overlay | P1, the WebGl.Renderer texture seam |

## What bites

- **Record file names, request standard widths.** A hand-built width is a 400.
- **Never trust a title without a position.** A designation string collides across catalogues (`B 33`).
- **The summary has no licence.** A credit line needs Commons `extmetadata`.
- **Do not bake pixels.** Licences differ per file and the images change upstream; the table carries the
  pointer and the credit, the client fetches the picture.
