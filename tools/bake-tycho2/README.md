# bake-tycho2

Slices the Tycho-2 catalog into region-aligned lzip members so the browser can fetch the sky it is
looking at instead of all 2.5M stars. Run by the "Stage Tycho-2 for the Sky Atlas" step in
`.github/workflows/pages.yml`. Design and measurements: [`docs/plans/web-tycho2.md`](../../docs/plans/web-tycho2.md).

```bash
dotnet run --project tools/bake-tycho2/BakeTycho2.csproj -c Release -- \
  src/TianWen.Lib/Astrometry/Catalogs/tyc2.bin.lz src/TianWen.UI.Web/wwwroot/tyc2 [targetMemberBytes]
```

Emits into the output directory:

| file | what |
|---|---|
| `manifest.bin` | member framing: count, raw length, each member's first region (688 bytes) |
| `m0000.lz` | the catalog **header** (the per-region offset table), always fetched |
| `m0001.lz` … | one member per ~256 KB of region-aligned records (166 at the default) |

## Five things that are easy to get wrong

**It reads the committed `.lz`, not the upstream catalog.** The members are a repack of the exact
bytes the desktop embeds, so the two encodings cannot drift into disagreeing about the sky. The tool
decompresses, repacks, and **verifies the concatenated members decode back to the identical input
before writing anything** — an unverified repack would be a second source of truth in disguise.

**Members are cut on region boundaries, never on a fixed stride.** `LzipOptions.MemberSize` would cut
wherever the byte count landed, and a member that splits a GSC region puts half a region behind a
boundary no client can address. The target is a floor the packer rounds up to the next region edge.

**Files, not byte ranges.** Ranges are unusable on both candidate hosts: GitHub Pages answers `206`
over the **gzip** representation (Fastly compresses every content type, and `Accept-Encoding` is a
forbidden header name so no browser can ask for identity), and Cloudflare Pages ignores `Range`
outright. **Do not re-test this with `curl` and conclude otherwise** — curl's default headers are
exactly what hides it.

**The manifest is not optional.** lzip members are only enumerable by walking *backwards* from the
end of the file (`LzipDecoder.FindMembers` reads each trailer's `member_size`), so a client holding
the head of the catalog cannot find a single member boundary. It carries framing only; the
sky-to-region question is answered by the GSC bounds table, which is already embedded in the assembly
and needs no fetch.

**256 KB is measured, not assumed.** It costs +1.4% on the asset and a wide view 69 files / 11.87 MiB
against an unconditional 28.7 MiB. 64 KB downloads 2.2 MiB less but costs 213 requests instead of 69,
on exactly the view where the user is waiting. Re-derive with `TIANWEN_TYC2_BAKE_PROBE=1` on
`Tycho2RegionBakeProbe` if the catalog or the host changes.
