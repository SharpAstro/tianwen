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
├── models/             # AI ONNX models (ModelResolver; also finds GraXpert's model in GraXpert's own cache)
├── Secrets/            # 0600 file per device secret, non-Windows or a TIANWEN_DATA_ROOT tree (else Credential Manager)
├── node.sock           # The machine's node's socket (NodeSocket), owner-only on Unix
├── node.lock           # One node per socket: held for the node's life, never deleted (NodeLock)
├── node-settings.json  # The node's own state: "Share this rig on the LAN" and its active profile (NodeSettings)
├── node.journal        # The node's crash journal: what it holds, gone once it holds nothing (NodeJournal)
└── lan-node-id.txt     # tianwen-server's stable LAN NodeId, the key remote-rig bindings persist against
```
