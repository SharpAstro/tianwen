# bake-milkyway

Turns the committed Milky Way texture into the PNG the browser sky map loads. Run by the "Stage the
Milky Way texture" step in `.github/workflows/pages.yml`. Design: Phase 6 of
[`docs/plans/skymap-milkyway.md`](../../docs/plans/skymap-milkyway.md).

```bash
dotnet run --project tools/bake-milkyway/BakeMilkyWay.csproj -c Release -- \
  src/TianWen.UI.Gui/Resources/milkyway.bgra.lz src/TianWen.UI.Web/wwwroot/milkyway.png
```

Run it by hand for a local `dotnet run` of the web project; without the PNG the atlas simply draws no
Milky Way, the same way the desktop does without the `.lz`.

## Three things that are easy to get wrong

**It reads the committed `.lz`, not the catalogues.** Desktop and web draw the same sky from the same
bytes. Re-baking for the web would be a second source of truth. The tool decodes its own PNG back and
refuses to write on any mismatch.

**The PNG is opaque and premultiplied.** The texture's alpha is brightness, not coverage, and the desktop
blend multiplies colour by it. A PNG with a real alpha channel leaves the browser to choose whether to
premultiply on decode, and un-premultiplying loses precision at exactly the low alphas this texture is
made of. So the colour is multiplied by alpha here, once, and the channel is dropped.

**The `.lz` is a Git LFS object.** A checkout without `git lfs pull` holds a pointer stub, which fails to
decode as lzip; the workflow step checks the size first so the failure names the cause.
