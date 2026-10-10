# Runtime data (AppData)

The tree under `%LOCALAPPDATA%/TianWen/` (`TianWenDataRoot`), or the folder `TIANWEN_DATA_ROOT` names. Add a directory here when you add one in code: nothing creates them from one place.

```
TianWen/
├── Logs/<date>/        # <appName>_<timestamp>.log per process and day: GUI_*, Server_*, Keeper_*, ... (FileLoggerProvider; rolls at local midnight)
├── Profiles/           # Per-profile data (*.json + NeuralGuider/*.ngm + BacklashHistory/*.json)
├── Planner/            # Pinned targets: <profileId>/<date>.json, remote rigs under rigs/<bindingId>/
├── Session/            # Session-setup state, <profileId>.json (SessionPersistence)
├── Guider/             # Guider frames dumped for plate solving (guider_*.fits + .ini)
├── Weather/            # OpenMeteo / OpenWeatherMap forecast cache
├── ObjectImages/       # Wikimedia object pictures, one file per (image, standard width) (ObjectPictureStore)
├── SmallBodies/        # JPL SBDB comet cache: comets.json + apparitions.json
├── Viewer/             # tianwen-fits' own: planetary-telescope.json, the Best stack panel's telescope (PlanetaryTelescopePersistence)
├── Planetary/          # telescopes.json: the telescope each camera was last said to be on, which AUTO reads (PlanetaryTelescopeMemory, #1391)
├── models/             # AI ONNX models (ModelResolver; also finds GraXpert's model in GraXpert's own cache)
├── Secrets/            # 0600 file per device secret, non-Windows or a TIANWEN_DATA_ROOT tree (else Credential Manager)
├── node.sock           # The machine's node's socket (NodeSocket), owner-only on Unix
├── node.lock           # One node per socket: held for the node's life, never deleted (NodeLock)
├── node-settings.json  # The node's own state: "Share this rig on the LAN" and its active profile (NodeSettings)
├── node.journal        # The node's crash journal: what it holds, gone once it holds nothing (NodeJournal)
└── lan-node-id.txt     # tianwen-server's stable LAN NodeId, the key remote-rig bindings persist against
```

## Who creates these, and how a file here is written (moved from CLAUDE.md, 2026-10-09)

CLAUDE.md keeps the rules in short form; this is its section's full text as it stood, moved verbatim ("here" in its first paragraph is CLAUDE.md, "there" the code).

`%LOCALAPPDATA%/TianWen/` (`TianWenDataRoot`), or the folder `TIANWEN_DATA_ROOT` names: a whole tree kept
apart from the user's, which a test's node runs on and a spawned node inherits. The whole set, because there
is **no** single choke point that creates these: `IExternal.CreateSubDirectoryInAppDataFolder(name)` covers
four of them, `Planner`/`Session` are built from `AppDataFolder` directly, `Profiles`/`Logs` off
`SharedStaticData.CommonDataRoot`, and `models` + `lan-node-id.txt` + the node's socket and lock are resolved by
their own owners. Add a directory here when you add one there.
Folders (annotated tree: `docs/architecture/runtime-data.md`, **add a directory there when you add one**): `Logs/<date>/` (`<appName>_<timestamp>.log` per process and day, rolls at local midnight), `Profiles/` (`*.json`, `NeuralGuider/*.ngm`, `BacklashHistory/*.json`), `Planner/` (`<profileId>/<date>.json`, remote rigs under `rigs/<bindingId>/`), `Session/` (`<profileId>.json`), `Guider/`, `Weather/`, `ObjectImages/`, `SmallBodies/` (`comets.json` + `apparitions.json`), `Viewer/` (`planetary-telescope.json`, `tianwen-fits`' Best stack telescope), `models/` (AI ONNX; also finds GraXpert's model in its own cache), `Secrets/` (0600 file per device secret, non-Windows or under `TIANWEN_DATA_ROOT`), and the node's own files: `node.sock`, `node.lock` (held for the node's life, never deleted), `node-settings.json`, `node.journal` (its crash journal, gone once it holds nothing), `lan-node-id.txt` (`tianwen-server`'s stable LAN NodeId, the key remote-rig bindings persist against).

**Every file here has more than one PROCESS on it** (the GUI, the TUI, the CLI, the server, the viewer, MCP), so it
is written with `IExternal.AtomicWriteJsonAsync` and read with `TryReadJsonAsync`, or `SharedFile` (`TianWen.Lib/IO`)
underneath both, never a bare `FileStream`. A write stages under a name of its own and replaces the file with the
POSIX-semantics rename on Windows, because `File.Move` refuses to replace a file any reader holds open, delete sharing
or not; a read shares read, write and delete, without which even that rename is refused; and **a reader believes a
file is gone only after looking again** (`SharedFile.TryOpenReadAsync` / `ListAsync`), because an NTFS replace, either
rename, hides the name for a moment and a planner that read "no pins" would save that back. **Do not replace those
looks with a reader/writer lock**: measured, the real-time scanner then holds a replaced file in kernel mode and every
rename onto it is refused for minutes. **A file every host ADDS to** (the comet apparition cache) goes through
`UpdateJsonAsync`, which holds its directory's `.lock` across the read, the merge and the write; a whole-file write
there drops what another host added. P0c item 3 of `docs/plans/hardware-in-the-server.md`; pinned by
`SharedAppDataFileTests`.
