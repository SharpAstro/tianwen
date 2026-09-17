# bake-object-imagery

Bakes `src/TianWen.Lib/Astrometry/Catalogs/object_articles.gs.gz`: for every catalogued object in scope,
the English Wikipedia article it is **verified** to have, the article's lead image, and that image's
credit. `ICelestialObjectDB.TryGetArticle` reads it; the planner's name link is the first consumer, and
the selection panel's picture and credit line (P1 of `docs/plans/object-imagery.md`) the next.

```bash
dotnet run --project tools/bake-object-imagery/BakeObjectImagery.csproj -c Release -- \
  --output src/TianWen.Lib/Astrometry/Catalogs/object_articles.gs.gz > bake-report.md
```

Run it by hand and commit the output. Not per deploy: Wikipedia changes slowly, and a table rebaked on
every push is a table nobody reviewed. The table is binary, so **the report on stdout is the review**:
coverage, the Messier and Caldwell objects left without an article, the licence spread, lead images whose
file name says `map`, and every index added, removed or retitled since the table it replaced. Progress
goes to stderr. A full run is about 35 minutes of requests made one at a time (29,327 codes, 19,013
titles), and Wikidata's SPARQL endpoint answers 429 partway through the codes pass, which the bake waits
out with the server's own `Retry-After`.

## What it does, and why each step is the way it is

Every rule was measured first; the numbers are in the plan's "P0 measured" section.

1. **Scope**: every object with a common name, every NGC and IC entry named or not (since 2026-09-18:
   English Wikipedia has a stub with a survey image for a large share of the nameless ones), plus Messier
   and Caldwell by their main catalogue entries. Most of the named set is stars whose common name is a
   Bayer or Flamsteed designation, included by decision.
2. **Candidates by catalogue code** (Wikidata `P528`), spelled Wikidata's way, which is neither ours nor
   consistent: `M 42` but `M99`, `SH 2-25`, `Gum 33`, `B 33`. Caldwell asks nothing: its Wikidata codes
   carry cluster designations, and a bare `C 99` answers a mazurka.
3. **Candidates by English Wikipedia title** (`Messier N`, NGC and IC designations, `Sh 2-N`, the common
   names), redirects followed and disambiguation pages dropped. This is what finds items with no codes at
   all: M100, M110, the Hyades and the Coalsack.
4. **Verification by position**: a candidate counts only when its Wikidata right ascension and declination
   lie within the object's catalogued major axis, or, with no size, one degree for an extended kind and ten
   arcminutes otherwise. Several survivors: found by both routes beats code only beats title only, then the
   nearer wins.
5. **The lead image** the article itself shows (`prop=pageimages`), not Wikidata's `P18`, which agreed with
   it for only a third of articles. Dropped, keeping the article's link: an SVG (every one measured was a
   constellation map); any format but JPEG, PNG and TIFF, which are what the desktop store decodes; a CHART, which is what star articles often lead with (a light curve, a position
   chart, a constellation map), flagged by Commons' own categories or by the file name, since each catches
   a few the other misses; a file with no licence, which no credit line can state; and a file Commons does
   not hold. Licence, artist, credit and the attribution flag come from Commons `extmetadata`, as plain
   text.

No pixels are baked. Licences differ per file and the files change upstream; the table carries the file
name and the credit, and the client asks Wikimedia for a standard thumbnail width at runtime.

## Being a good client

Requests go one at a time, with a pause between them, under a User-Agent naming this project, which is what
Wikimedia's policy asks of a bulk client. A throttle, a server error or a timeout is retried with a growing
pause and honours `Retry-After`; a request timeout is told apart from cancellation by the token, not by the
exception type (the lesson `bake-comets` paid for).
