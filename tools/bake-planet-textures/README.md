# bake-planet-textures

Bakes `src/TianWen.UI.Abstractions/Resources/planet-textures.png`, the sky atlas's OFFLINE planet colour maps
(docs/plans/atlas-planet-detail.md, "A4: the textured disc", #1037). One 512 x 512 PNG, eight cells of 256 x 128,
two to a row in `PlanetTextureAtlas.Layout` order (Mercury, Venus / Moon, Mars / Jupiter, Saturn / Uranus,
Neptune), each equirectangular in `PlanetMap`'s convention: planetographic latitude, row 0 the north edge, WEST
longitude with column 0 at 0 degrees and longitude falling to the right. The Sun needs no texture.

```
dotnet run --project tools/bake-planet-textures -c Release -- <download folder> src/TianWen.UI.Abstractions/Resources/planet-textures.png
```

Each source is downloaded into the folder unless already there, read in its own longitude convention (stated in
`Program.cs`, never shifted by hand), its holes filled where it has any (`PlanetColourMap.FromRgb8(fillHoles:
true)`), area-averaged to the cell, and written through `PlanetTextureAtlas.Encode`. The bake prints the file's
size and each body's mean colour: that is the review, since the PNG is binary. Re-bake by hand and commit.

Size on 2026-10-14: 301,799 bytes.

## Sources and licences

| Body | Source | Licence / credit |
|---|---|---|
| Mercury | Solar System Scope, `2k_mercury.jpg` (https://www.solarsystemscope.com/textures/) | CC BY 4.0, Solar System Scope, "based on NASA elevation and imagery data" (MESSENGER) |
| Venus | Solar System Scope, `2k_venus_atmosphere.jpg` | CC BY 4.0, Solar System Scope. A cloud deck has no fixed longitude; the visible disc is nearly featureless, as it is |
| Moon | NASA Scientific Visualization Studio, CGI Moon Kit, `lroc_color_poles_1k.jpg` (https://svs.gsfc.nasa.gov/4720) | Public domain, NASA's Scientific Visualization Studio, LRO LROC WAC |
| Mars | Solar System Scope, `2k_mars.jpg` | CC BY 4.0, Solar System Scope, "based on NASA elevation and imagery data" (Viking, MGS) |
| Jupiter | OPAL 2025b colour composite (F395N, F502N, F631N), `globalmap-small.tif` | NASA/ESA HST, OPAL program; see the acknowledgement below. A colour RENDERING: the filters are not the eye's |
| Saturn | OPAL 2025b colour composite (F395N, F502N, F631N) | as Jupiter |
| Uranus | OPAL 2025a colour composite (F657N, F547M, F467M) | as Jupiter |
| Neptune | OPAL 2025c colour composite (F467M, F547M, F657N) | as Jupiter |

The four OPAL maps are the same files the runtime upgrade fetches (`OpalColourMaps`), at the atlas's size, so the
offline texture and the upgrade differ only in resolution. Their URLs are in `OpalColourMaps.TryFor`.

OPAL's requested acknowledgement: "This work used data acquired from the NASA/ESA HST Space Telescope, associated
with OPAL program (PI: Simon, GO13937), and archived by the Space Telescope Science Institute, which is operated by
the Association of Universities for Research in Astronomy, Inc., under NASA contract NAS 5-26555. All maps are
available at http://dx.doi.org/10.17909/T9G593."

## Conventions checked

- OPAL: "left edge = 0 System III W. longitude, decreasing to the right" (the 2025 Jupiter readme), which is
  `PlanetMap`'s own; the Great Red Spot reads at west 321 degrees System III in the 2025b map, where its System II
  longitude at the map's date puts it (pinned by `PlanetTextureAtlasTests`).
- Solar System Scope and the SVS Moon Kit are centred on 0 degrees with east growing to the right: on Mars the
  darkest column at +8 degrees (Syrtis Major, 69 E) sits at 0.72 of the width, where (180 + 69) / 360 = 0.69.
- OPAL's composites are black where Hubble never saw the planet (Uranus's southern hemisphere in 2025, Saturn's north
  pole); the fill holds the nearest kept rows' mean colour there, never a row's longitude pattern.
