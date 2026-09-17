# Object imagery from Wikipedia and Wikimedia Commons (plan)

**Status: NOT STARTED. Written 2026-09-17** from the astrophoto.app field note in
[inbox.md](../todo/inbox.md) ("Field note: astrophoto.app"), after reading that site's shipped JavaScript
and measuring every endpoint below. Raised by the user: the viewer and the atlas should show an object's
picture the way that site does, with attribution, positioned on the sky where we can, and the identity
of "which article is this object" decided once at bake time instead of guessed at click time.

Companions: [in-app-sky-atlas](in-app-sky-atlas.md) (the selection panel this feeds),
[web-showcase](web-showcase.md) (the browser build), [skymap-milkyway](skymap-milkyway.md) (the other
imagery layer, and its browser phase, which lands the texture seam this plan reuses).

## What exists today

- `PlannerDetails.GetWikipediaUrl` builds `https://en.wikipedia.org/wiki/` + the object's MAIN catalogue
  designation (`CatalogIndex.ToCanonical()`, spaces to `_`). It is a GUESS: the planner's name line links
  there and nothing checks the page exists or is about this object. `PlannerDetailsTests` pins only the
  URL shape.
- Nothing fetches a summary, an image, or a licence. The atlas info panel and the viewer's selection
  panel (`ObjectInfoPanel`) show catalogue data only.

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

## Design

### P0: the identity bake (the part that replaces guessing)

A build-time tool, `tools/bake-object-imagery`, run on demand and committed (not per deploy: Wikipedia
changes slowly and a bake that reruns every push would churn a table nobody reviewed). For every catalogue
object worth a picture:

1. Try **every designation we already hold for it**: the main index and its cross-indices
   (`M 8`, `NGC 6523`, `Sh2-25` are one object to us).
2. Candidates from both sources: Wikidata `P528` with the `P972` catalogue qualifier checked against the
   catalogue we meant, and the Wikipedia title (redirects followed, disambiguation pages rejected by the
   summary's `type`).
3. **Accept a candidate only when its sky position agrees with ours**, from Wikidata's right ascension
   (`P6257`) and declination (`P6258`), within a tolerance scaled by the object's catalogued size. This is
   what makes the answer a verification instead of a guess. How many astronomical items carry both
   properties is unmeasured and is P0's first number.
4. Record per object: Wikidata item, English article title, lead image file name, its licence short name,
   artist, credit text, `AttributionRequired`, and the image's pixel size.

Output: a compact embedded table in `TianWen.Lib` keyed by `CatalogIndex`, same shape and loading path
as the other baked catalogue snapshots. No pixels. `GetWikipediaUrl` reads the table and stops guessing;
an object absent from the table gets no link rather than a dead one.

### P1: the picture in the selection panel

- The atlas info panel and the viewer's selection panel (`ObjectInfoPanel`) show the thumbnail, and a
  click opens the large image, which is the astrophoto.app interaction the user pointed at.
- **A credit line under every image**, built from the bake: "ESO/S. Guisard, CC BY 4.0", linking to the
  Commons file page. The help menu additionally lists the images shown this session. A help-menu list
  alone is thin for CC BY and CC BY-SA, which is what most of these images are.
- Fetched on selection, never pre-fetched. Desktop caches under a new `AppData/TianWen/ObjectImages/`
  (add it to the CLAUDE.md AppData list and to whatever owns the others); the browser relies on its HTTP
  cache.
- Offline or blocked: the panel shows what it shows today. No retry storm: remember a failed file for the
  session, the lesson `ApparitionRetryCooldown` was written for.

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
