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

    // A member's entry is keyed by its CONTENT as well as its number: `tag` is the CRC32 the manifest
    // gives it, so an entry decoded from an earlier bake of the catalog is simply never found.
    function memberKey(version, member, tag) {
        return version + ":m" + member + ":" + tag;
    }

    function openDb() {
        return new Promise(function (resolve, reject) {
            const req = indexedDB.open(DB_NAME, 1);
            req.onupgradeneeded = function () { req.result.createObjectStore(STORE); };
            req.onsuccess = function () { resolve(req.result); };
            req.onerror = function () { reject(req.error); };
        });
    }

    return {
        // Cheap existence + version check. The C# side calls this BEFORE opening the stream, so a
        // miss never produces a zero-length stream (Blazor's OpenReadStreamAsync rejects length 0).
        has: async function (version) {
            try {
                const db = await openDb();
                const rec = await new Promise(function (resolve, reject) {
                    const req = db.transaction(STORE, "readonly").objectStore(STORE).get(KEY);
                    req.onsuccess = function () { resolve(req.result); };
                    req.onerror = function () { reject(req.error); };
                });
                db.close();
                return !!(rec && rec.version === version && rec.bytes && rec.bytes.byteLength > 0);
            } catch (e) {
                console.warn("[tianwen-web] tyc2 cache has() failed:", e);
                return false;
            }
        },

        // Returns the cached bytes as a Uint8Array (Blazor hands it to C# as an IJSStreamReference).
        // Only called after has() reports a hit, so the record exists; on a defensive miss it still
        // returns an empty array (the C# side treats a zero read as a miss).
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

        // ---- Per-member cache (the incremental atlas) ----------------------------------------
        //
        // The member path never reaches the whole-catalog entries above, so without these a repeat
        // visit re-fetched and, more to the point, RE-DECODED every member it had already seen --
        // measured at 1183 ms of blocked main thread over three pans. These store the DECOMPRESSED
        // member bytes, so a hit skips the lzip decode entirely, which is the expensive half.
        // Keyed `<version>:m<member>:<tag>` in the same store, the tag being the member's CRC32 from
        // the manifest; retainMembers drops every entry another version or an earlier bake left.

        // Which of `members` are already cached, as a parallel array of booleans. One transaction
        // for the whole set: asking per member is a transaction each, which costs more than the
        // decode it is trying to avoid. `tags` runs parallel to `members`.
        hasMembers: async function (version, members, tags) {
            const out = new Array(members.length).fill(false);
            try {
                const db = await openDb();
                await new Promise(function (resolve, reject) {
                    const tx = db.transaction(STORE, "readonly");
                    const store = tx.objectStore(STORE);
                    members.forEach(function (m, i) {
                        // getKey, not get: this must not deserialize ~260 KB per member just to
                        // answer a yes/no, which is the whole reason the probe is separate.
                        const req = store.getKey(memberKey(version, m, tags[i]));
                        req.onsuccess = function () { out[i] = req.result !== undefined; };
                    });
                    tx.oncomplete = function () { resolve(); };
                    tx.onerror = function () { reject(tx.error); };
                });
                db.close();
            } catch (e) {
                console.warn("[tianwen-web] tyc2 member cache probe failed:", e);
            }
            return out;
        },

        // Decompressed bytes for one member, or an empty array on any miss/failure.
        loadMember: async function (version, member, tag) {
            try {
                const db = await openDb();
                const rec = await new Promise(function (resolve, reject) {
                    const req = db.transaction(STORE, "readonly").objectStore(STORE).get(memberKey(version, member, tag));
                    req.onsuccess = function () { resolve(req.result); };
                    req.onerror = function () { reject(req.error); };
                });
                db.close();
                return rec && rec.byteLength ? new Uint8Array(rec) : new Uint8Array(0);
            } catch (e) {
                console.warn("[tianwen-web] tyc2 member load failed:", e);
                return new Uint8Array(0);
            }
        },

        // Persist one member's DECOMPRESSED bytes. Best-effort and fire-and-forget from C#: a quota
        // failure must leave the atlas working, just uncached.
        saveMember: async function (version, member, tag, streamRef) {
            try {
                const buf = await streamRef.arrayBuffer();
                const db = await openDb();
                await new Promise(function (resolve, reject) {
                    const tx = db.transaction(STORE, "readwrite");
                    tx.objectStore(STORE).put(buf, memberKey(version, member, tag));
                    tx.oncomplete = function () { resolve(); };
                    tx.onerror = function () { reject(tx.error); };
                });
                db.close();
            } catch (e) {
                console.warn("[tianwen-web] tyc2 member save failed:", e);
            }
        },

        // Deletes every member entry that is not the current bake's: another version's, one keyed before
        // members carried a tag, or one with a tag the manifest no longer gives (`tags[m]` is member m's).
        // A re-bake leaves its predecessor's entries unreachable, so without this they would sit on the
        // user's disk for ever, up to the whole catalog again per re-bake. The whole-catalog entry (KEY)
        // is the fallback path's and is left alone. Best-effort, like every write here.
        retainMembers: async function (version, tags) {
            try {
                const keep = new Set(tags.map(function (tag, m) { return memberKey(version, m, tag); }));
                const db = await openDb();
                let deleted = 0;
                await new Promise(function (resolve, reject) {
                    const tx = db.transaction(STORE, "readwrite");
                    const store = tx.objectStore(STORE);
                    const req = store.openKeyCursor();
                    req.onsuccess = function () {
                        const cursor = req.result;
                        if (!cursor) return;
                        if (cursor.key !== KEY && !keep.has(cursor.key)) {
                            store.delete(cursor.key);
                            deleted++;
                        }
                        cursor.continue();
                    };
                    tx.oncomplete = function () { resolve(); };
                    tx.onerror = function () { reject(tx.error); };
                });
                db.close();
                return deleted;
            } catch (e) {
                console.warn("[tianwen-web] tyc2 member cache sweep failed:", e);
                return 0;
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
