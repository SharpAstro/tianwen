// IndexedDB cache for the RAW DECOMPRESSED Tycho-2 catalog (~42 MB). Keyed by a catalog version so
// a catalog change invalidates it. On a repeat visit this skips BOTH the ~30 MB fetch AND the lzip
// decompress -- the app feeds the cached bytes straight into the DB (star records + spatial index)
// and re-flattens to the GPU, so clicking a star to identify it works on a cached load too. The
// bytes cross the JS<->.NET boundary via Blazor's stream interop (IJSStreamReference on load,
// DotNetStreamReference on save), never slow JSON marshaling.
//
// localStorage is unusable here (~5-10 MB, strings only); IndexedDB holds hundreds of MB async and
// survives reloads/redeploys/eviction far better than the HTTP cache.
window.tyc2Cache = (function () {
    const DB_NAME = "tianwen-atlas";
    const STORE = "tyc2";
    const KEY = "stars";

    function openDb() {
        return new Promise(function (resolve, reject) {
            const req = indexedDB.open(DB_NAME, 1);
            req.onupgradeneeded = function () { req.result.createObjectStore(STORE); };
            req.onsuccess = function () { resolve(req.result); };
            req.onerror = function () { reject(req.error); };
        });
    }

    return {
        // Returns the cached bytes as a Uint8Array (Blazor hands it to C# as an IJSStreamReference)
        // when a record for `version` exists, otherwise an EMPTY array -- the C# side reads a
        // zero-length stream as a miss and falls back to fetch+decode. Returning empty rather than
        // null keeps the return type a plain stream (no nullable-marshaling ambiguity).
        load: async function (version) {
            try {
                const db = await openDb();
                const rec = await new Promise(function (resolve, reject) {
                    const req = db.transaction(STORE, "readonly").objectStore(STORE).get(KEY);
                    req.onsuccess = function () { resolve(req.result); };
                    req.onerror = function () { reject(req.error); };
                });
                db.close();
                if (rec && rec.version === version && rec.bytes) {
                    return new Uint8Array(rec.bytes);
                }
                return new Uint8Array(0);
            } catch (e) {
                console.warn("[tianwen-web] tyc2 cache load failed:", e);
                return new Uint8Array(0);
            }
        },

        // `streamRef` is a .NET DotNetStreamReference; read it fully and persist under `version`.
        // Best-effort: a failure (private mode, quota) is swallowed so the atlas still works.
        save: async function (version, streamRef) {
            try {
                const buf = await streamRef.arrayBuffer();
                const db = await openDb();
                await new Promise(function (resolve, reject) {
                    const tx = db.transaction(STORE, "readwrite");
                    tx.objectStore(STORE).put({ version: version, bytes: buf }, KEY);
                    tx.oncomplete = function () { resolve(); };
                    tx.onerror = function () { reject(tx.error); };
                });
                db.close();
            } catch (e) {
                console.warn("[tianwen-web] tyc2 cache save failed:", e);
            }
        }
    };
})();
