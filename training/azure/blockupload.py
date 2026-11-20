"""Resumable upload of one large file to one block blob, continuing where a stopped run left off.

The blob is staged as numbered blocks (Put Block, ids by position) and committed once at the end (Put Block List). Before
sending anything, a run asks the service which blocks are already staged (Get Block List, uncommitted) and sends only the
missing ones, so stopping it, a reboot or changing the cap costs at most the blocks in flight. Each block carries its
Content-MD5, which the service checks. A blob already committed at the file's size is left alone.

Usage: python -I blockupload.py <file> <blob url without query> <sas file> [--cap-mbps N] [--workers 4] [--block-mib 8]
upload-cache.ps1 sends every file of 1 GB or more through it. Written 2026-10-21, when azcopy sent synth7's 21 GB tile file
from zero after every restart of its pass. Tested before use: a 64 MB file stopped after 45 s came back with 4 of 16 blocks
staged, the second run sent the other 12, and the blob read back byte for byte (SHA-256).

Each worker keeps ONE connection open across its blocks and opens a new one only after an error (#2300). A connection a
block was opened on used to be the common failure: 4 to 16 % of the attempts to the storage endpoint timed out whole after
21 s while the next one, from a new port, went through at once, and a cache of 3,300 blocks drew that lot 3,300 times. A
try that fails while connecting has sent nothing and is not charged to the cap.
"""
import argparse, base64, hashlib, http.client, io, os, sys, threading, time, urllib.error, urllib.parse, urllib.request
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor, as_completed

VERSION = "2021-08-06"
RETRY_PAUSE = 5.0   # seconds before the second try of a block; the n-th try waits n - 1 times this


def request(method, url, body=None, headers=None, timeout=300):
    req = urllib.request.Request(url, data=body, method=method)
    req.add_header("x-ms-version", VERSION)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.status, dict(r.headers), r.read()


def block_id(i):
    return base64.b64encode(f"blk{i:08d}".encode()).decode()


class Throttle:
    """Holds the total send rate to cap_mbps across every worker (0 is no cap)."""

    def __init__(self, cap_mbps):
        self.rate = cap_mbps * 1e6 / 8 if cap_mbps > 0 else 0
        self.lock = threading.Lock()
        self.next_free = time.monotonic()

    def wait(self, nbytes):
        if not self.rate:
            return
        with self.lock:
            now = time.monotonic()
            start = max(now, self.next_free)
            self.next_free = start + nbytes / self.rate
        delay = start - time.monotonic()
        if delay > 0:
            time.sleep(delay)


class Sender:
    """One worker's connection to the blob's host, kept open across blocks and opened again after an error."""

    def __init__(self, url, timeout=300):
        u = urllib.parse.urlsplit(url)
        self.scheme, self.host, self.port, self.timeout = u.scheme, u.hostname, u.port, timeout
        self.conn = None
        self.connects = 0

    def connect(self):
        """Opens the connection when there is none. A failure here has sent nothing."""
        if self.conn is not None:
            return
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


RETRIED = (urllib.error.URLError, http.client.HTTPException, TimeoutError, ConnectionError, OSError)


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
    p.add_argument("--workers", type=int, default=4)
    p.add_argument("--block-mib", type=int, default=8)
    a = p.parse_args(argv)
    sas = open(a.sas_file, encoding="ascii").read().strip().lstrip("?")
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
          f"{len(todo)} to send{f', capped at {a.cap_mbps:g} Mb/s' if a.cap_mbps else ''}", flush=True)

    throttle = Throttle(a.cap_mbps)
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
