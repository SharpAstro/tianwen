"""Resumable upload of one large file to one block blob, continuing where a stopped run left off.

The blob is staged as numbered blocks (Put Block, ids by position) and committed once at the end (Put Block List). Before
sending anything, a run asks the service which blocks are already staged (Get Block List, uncommitted) and sends only the
missing ones, so stopping it, a reboot or changing the cap costs at most the blocks in flight. Each block carries its
Content-MD5, which the service checks. A blob already committed at the file's size is left alone.

Usage: python -I blockupload.py <file> <blob url without query> <sas file> [--cap-mbps N] [--cap-file F] [--workers 4]
       [--block-mib 8]
With --cap-file the cap is live: the file is read again every 5 s, and a new number in it is the cap within seconds, so
the owner can throttle an upload that is running (and let it go again) without stopping it (2026-11-25).
upload-cache.ps1 sends every file of 1 GB or more through it. Written 2026-10-21, when azcopy sent synth7's 21 GB tile file
from zero after every restart of its pass. Tested before use: a 64 MB file stopped after 45 s came back with 4 of 16 blocks
staged, the second run sent the other 12, and the blob read back byte for byte (SHA-256).

Each worker keeps ONE connection open across its blocks and opens a new one only after an error (#2300). A connection a
block was opened on used to be the common failure: 4 to 19 % of the attempts to the storage endpoint timed out whole after
21 s while the next one, from a new port, went through at once, and a cache of 3,300 blocks drew that lot 3,300 times. A
try that fails while connecting has sent nothing and is not charged to the cap. The four calls outside the blocks (head,
block list, commit, head) still take a connection each, so each of them is tried up to five times.

A worker's connection sits idle while the worker waits for the cap (about 20 s at 10 Mb/s with 4 workers and 8 MiB blocks,
ten times that at 1 Mb/s). One that the far end or a NAT drops in that wait still has its socket, so it is found only by
the send, after the charge: that try is charged and costs one pause. How often that happens was not measured (#2316).

For a direct line only: the block connections go straight to the blob's host, while those four calls go through urllib,
which honours a proxy from the environment. Behind a proxy the four would pass and every block would fail.
"""
import argparse, base64, hashlib, http.client, io, os, sys, threading, time, urllib.error, urllib.parse, urllib.request
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor, as_completed

VERSION = "2021-08-06"
RETRY_PAUSE = 5.0   # seconds before the second try of a block or a call; the n-th try waits n - 1 times this
RETRIED = (urllib.error.URLError, http.client.HTTPException, TimeoutError, ConnectionError, OSError)


def request(method, url, body=None, headers=None, timeout=300):
    """One call on a connection of its own, up to five tries. A connection that will not open or that dies, and a 5xx,
    are tried again; any other HTTP status is an answer and is raised at once (a 404 says there is no such blob). Every
    call made through here can be sent twice: the head and the block list only read, and a block list committed a second
    time names blocks the service then finds among the committed ones, which leaves the same blob."""
    for attempt in range(1, 6):
        req = urllib.request.Request(url, data=body, method=method)
        req.add_header("x-ms-version", VERSION)
        for k, v in (headers or {}).items():
            req.add_header(k, v)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.status, dict(r.headers), r.read()
        except RETRIED as e:
            if attempt == 5 or (isinstance(e, urllib.error.HTTPError) and e.code < 500):
                raise
            print(f"  {method} try {attempt} failed ({e}); again in {RETRY_PAUSE * attempt:g} s", flush=True)
            time.sleep(RETRY_PAUSE * attempt)


def block_id(i):
    return base64.b64encode(f"blk{i:08d}".encode()).decode()


def read_cap(path, say=None):
    """The cap a cap file names, in Mb/s: a number over 0. None when the file is missing, being written, holds no number
    or holds 0 or less, so the caller keeps the cap it has: a slip in the file never takes the cap off. utf-8-sig takes
    plain ASCII and a BOM (an editor's, or Windows PowerShell 5.1 with -Encoding utf8). When the file exists and is not
    a usable number, say() is told why, so whoever wrote it can see in the log that it was not taken."""
    try:
        with open(path, encoding="utf-8-sig") as f:
            text = f.read().strip()
    except FileNotFoundError:
        return None
    except (OSError, UnicodeDecodeError) as e:
        if say:
            say(f"  cap file {path} is not read ({e.__class__.__name__}); write plain ASCII digits, the cap stays")
        return None
    try:
        cap = float(text)
    except ValueError:
        if say:
            say(f"  cap file {path} holds {text[:40]!r}, not a number; the cap stays")
        return None
    if cap > 0:
        return cap
    if say:
        say(f"  cap file {path} holds {cap:g}, 0 or less is ignored; the cap stays (write a high number to let go)")
    return None


class Throttle:
    """Holds the total send rate to cap_mbps across every worker (0 is no cap).

    Bytes are let out by a running count: the cap adds its rate to what is allowed as time passes, a block is given the
    place in that count where the blocks before it end, and it may start once the count has reached its place. Idle time
    earns nothing (the count never runs ahead of what was handed out), so a block that comes to an idle throttle starts at
    once, as before. With cap_file, the file is read again every `recheck` seconds and a new number is the cap from then
    on; a worker that waits sleeps in slices of at most `slice_s` seconds and looks again, so a lower cap holds the next
    blocks back and a higher one lets waiting workers go, within seconds, with nothing restarted. A block already sent is
    not slowed."""

    def __init__(self, cap_mbps, cap_file=None, recheck=5.0, slice_s=1.0, clock=time.monotonic, sleep=time.sleep,
                 say=print):
        self.cap = cap_mbps if cap_mbps > 0 else 0
        self.cap_file, self.recheck, self.slice_s = cap_file, recheck, slice_s
        self.clock, self.sleep, self.say = clock, sleep, say
        self.lock = threading.Lock()
        self.last = clock()
        self.allowed = 0.0
        self.handed = 0.0
        self.read_at = None
        self.complained = None
        self.complained_count = 0
        if cap_file:
            self._look(self.last)

    def rate(self):
        return self.cap * 1e6 / 8 if self.cap > 0 else 0.0

    def _complain(self, message):
        """A file problem is said once per distinct message, not at every look."""
        self.complained_count += 1
        if message != self.complained:
            self.complained = message
            self.say(message)

    def _look(self, now):
        self.read_at = now
        before = self.complained_count
        cap = read_cap(self.cap_file, self._complain)
        if self.complained_count == before:
            # A look with no complaint (a good number, or no file) ends the run of one slip, so the same slip written
            # again later is said again.
            self.complained = None
        if cap is not None and cap != self.cap:
            self.say(f"  cap now {cap:g} Mb/s (from {self.cap_file})")
            self.cap = cap

    def _advance(self, now):
        """Under the lock: the count moves on at the rate that held since the last look, then the file is read if due,
        so a new cap counts from now on."""
        rate = self.rate()
        self.allowed = min(self.handed, self.allowed + rate * (now - self.last)) if rate else self.handed
        self.last = now
        if self.cap_file and now - self.read_at >= self.recheck:
            self._look(now)

    def wait(self, nbytes):
        with self.lock:
            self._advance(self.clock())
            place = self.handed
            self.handed += nbytes
            if not self.rate():
                self.allowed = self.handed
                return
        while True:
            with self.lock:
                self._advance(self.clock())
                rate = self.rate()
                if not rate or self.allowed >= place:
                    return
                need = (place - self.allowed) / rate
            self.sleep(min(need, self.slice_s))


class Sender:
    """One worker's connection to the blob's host, kept open across blocks and opened again after an error."""

    def __init__(self, url, timeout=300):
        u = urllib.parse.urlsplit(url)
        self.scheme, self.host, self.port, self.timeout = u.scheme, u.hostname, u.port, timeout
        self.conn = None
        self.connects = 0

    def connect(self):
        """Opens the connection when none stands. A failure here has sent nothing. http.client drops its socket after an
        answer that carries Connection: close and would open a new one by itself inside the next request, after the cap
        was charged and without being counted, so a connection with no socket counts as none and is opened here."""
        if self.conn is not None:
            if self.conn.sock is not None:
                return
            self.close()
        kind = http.client.HTTPSConnection if self.scheme == "https" else http.client.HTTPConnection
        conn = kind(self.host, self.port, timeout=self.timeout)
        conn.connect()
        self.conn = conn
        self.connects += 1

    def close(self):
        conn, self.conn = self.conn, None
        if conn is not None:
            conn.close()

    def put(self, target, body, headers):
        """PUT on the open connection. Any error, an HTTP error status included, closes it, so the next try opens a new
        one; an HTTP error is raised as urllib's HTTPError, as request() raises it."""
        try:
            self.conn.request("PUT", target, body=body, headers={"x-ms-version": VERSION, **headers})
            r = self.conn.getresponse()
            answer = r.read()
        except Exception:
            self.close()
            raise
        if r.status >= 300:
            self.close()
            raise urllib.error.HTTPError(f"{self.scheme}://{self.host}{target.split('?')[0]}", r.status, r.reason, r.headers, io.BytesIO(answer))
        return r.status


def send_block(sender, throttle, target, data, headers, say=print, what="block"):
    """One block through a worker's connection, up to eight tries. The cap is charged only once the connection stands:
    a try that fails while connecting has sent nothing. Returns the number of tries it took."""
    for attempt in range(1, 9):
        try:
            sender.connect()
            throttle.wait(len(data))
            sender.put(target, data, headers)
            return attempt
        except RETRIED as e:
            if attempt == 8:
                raise
            say(f"  {what} try {attempt} failed ({e}); again in {RETRY_PAUSE * attempt:g} s")
            time.sleep(RETRY_PAUSE * attempt)


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("file")
    p.add_argument("url")
    p.add_argument("sas_file")
    p.add_argument("--cap-mbps", type=float, default=0)
    p.add_argument("--cap-file", default=None,
                   help="a file holding the cap in Mb/s, read again every 5 s while the run goes on: a new number over 0 "
                        "is the cap within seconds, with nothing restarted; a missing or unreadable file, or 0 or less, "
                        "keeps the cap the run has")
    p.add_argument("--workers", type=int, default=4)
    p.add_argument("--block-mib", type=int, default=8)
    a = p.parse_args(argv)
    with open(a.sas_file, encoding="ascii") as f:
        sas = f.read().strip().lstrip("?")
    size = os.path.getsize(a.file)
    bs = a.block_mib * 1024 * 1024
    n = (size + bs - 1) // bs
    blob = f"{a.url}?{sas}"

    try:
        _, h, _ = request("HEAD", blob)
        if int(h.get("Content-Length", -1)) == size:
            print(f"{os.path.basename(a.file)}: already committed at {size} bytes, nothing to send", flush=True)
            return 0
    except urllib.error.HTTPError as e:
        if e.code != 404:
            raise

    staged = set()
    try:
        _, _, body = request("GET", f"{blob}&comp=blocklist&blocklisttype=uncommitted")
        for b in ET.fromstring(body).iter("Block"):
            name, bsize = b.findtext("Name"), int(b.findtext("Size"))
            staged.add((name, bsize))
    except urllib.error.HTTPError as e:
        if e.code != 404:
            raise
    want = [(i, block_id(i), min(bs, size - i * bs)) for i in range(n)]
    ours = {bid for _, bid, _ in want}
    foreign = [name for name, _ in staged if name not in ours]
    if foreign:
        # Another tool's uncommitted blocks (a stopped azcopy) name blocks differently, and the service then refuses
        # ours (400: every block id of one blob must be the same length). Committing an empty list discards every
        # uncommitted block, ours included, and leaves a 0-byte blob that this run overwrites.
        print(f"{os.path.basename(a.file)}: {len(foreign)} uncommitted blocks from another uploader; discarding them",
              flush=True)
        empty = b'<?xml version="1.0" encoding="utf-8"?><BlockList></BlockList>'
        request("PUT", f"{blob}&comp=blocklist", empty, {"Content-Length": str(len(empty)), "Content-Type": "application/xml"})
        staged = set()
    todo = [w for w in want if (w[1], w[2]) not in staged]
    print(f"{os.path.basename(a.file)}: {size} bytes in {n} blocks of {a.block_mib} MiB; {n - len(todo)} already staged, "
          f"{len(todo)} to send{f', capped at {a.cap_mbps:g} Mb/s' if a.cap_mbps else ''}"
          f"{f', the cap read again from {a.cap_file}' if a.cap_file else ''}", flush=True)

    throttle = Throttle(a.cap_mbps, a.cap_file, say=lambda line: print(line, flush=True))
    done = [n - len(todo)]
    sent = [0]
    t0 = time.monotonic()
    lock = threading.Lock()
    local = threading.local()
    senders = []
    path = urllib.parse.urlsplit(a.url).path

    def say(line):
        print(line, flush=True)

    def send(i, bid, length):
        with open(a.file, "rb") as f:
            f.seek(i * bs)
            data = f.read(length)
        md5 = base64.b64encode(hashlib.md5(data).digest()).decode()
        if not hasattr(local, "sender"):
            local.sender = Sender(a.url)
            with lock:
                senders.append(local.sender)
        send_block(local.sender, throttle, f"{path}?{sas}&comp=block&blockid={urllib.parse.quote(bid)}", data,
                   {"Content-Length": str(length), "Content-MD5": md5}, say, f"block {i}")
        with lock:
            done[0] += 1
            sent[0] += length
            if done[0] % 50 == 0 or done[0] == n:
                el = time.monotonic() - t0
                print(f"  {done[0]}/{n} blocks ({100 * done[0] / n:.1f} %), {sent[0] * 8 / el / 1e6:.1f} Mb/s this run",
                      flush=True)

    try:
        with ThreadPoolExecutor(a.workers) as ex:
            for fut in as_completed([ex.submit(send, *w) for w in todo]):
                fut.result()
    finally:
        for s in senders:
            s.close()
    if todo:
        print(f"  {sum(s.connects for s in senders)} connections opened for {len(todo)} blocks", flush=True)

    body = ("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>"
            + "".join(f"<Latest>{bid}</Latest>" for _, bid, _ in want) + "</BlockList>").encode()
    request("PUT", f"{blob}&comp=blocklist", body, {"Content-Length": str(len(body)), "Content-Type": "application/xml"})
    _, h, _ = request("HEAD", blob)
    if int(h.get("Content-Length", -1)) != size:
        print(f"FAILED: committed {h.get('Content-Length')} bytes, expected {size}", flush=True)
        return 1
    print(f"{os.path.basename(a.file)}: committed, {size} bytes", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
