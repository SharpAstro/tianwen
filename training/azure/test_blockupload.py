"""blockupload.py against a local stand-in for the blob service: connections are kept, a dropped one is opened again,
a try that fails while connecting is not charged to the cap, and the calls outside the blocks are tried again (#2300).

    cd training/azure && python -m unittest test_blockupload -v
"""
import base64, contextlib, io, hashlib, os, socket, tempfile, threading, unittest, urllib.error, urllib.parse
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import blockupload as B


class Blob:
    """What the stand-in service holds for its one blob, and what it saw."""

    def __init__(self):
        self.lock = threading.Lock()
        self.staged = {}          # block id -> bytes
        self.kept = {}            # block id -> bytes of the committed blob
        self.committed = None     # bytes once Put Block List came
        self.connections = 0
        self.block_puts = 0
        self.single_calls = 0     # the calls outside the blocks: head, block list, commit
        self.drop_once = set()    # block ids whose first Put Block is read and then dropped without an answer
        self.refuse_once = set()  # block ids whose first Put Block is answered 500
        self.close_after = set()  # block ids whose Put Block is answered 201 with Connection: close
        self.drop_single = {}     # "HEAD" / "GET" / "COMMIT" -> how many of the next such calls get no answer
        self.busy_single = {}     # "HEAD" / "GET" / "COMMIT" -> how many of the next such calls are answered 503
        self.lose_commit_answer = 0  # how many of the next commits are carried out and then get no answer
        self.head_status = None   # a status every head is answered with, in place of the blob's own


def handler_for(blob):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def setup(self):
            super().setup()
            with blob.lock:
                blob.connections += 1

        def log_message(self, *args):
            pass

        def answer(self, status, body=b"", headers=None):
            self.send_response(status)
            for k, v in (headers or {}).items():
                self.send_header(k, v)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(body)

        def query(self):
            return dict(urllib.parse.parse_qsl(urllib.parse.urlsplit(self.path).query))

        def single(self, kind):
            """Counts a call outside the blocks; true when this one is not served: it gets no answer (the client sees
            the line die) or a 503."""
            with blob.lock:
                blob.single_calls += 1
                drop = blob.drop_single.get(kind, 0) > 0
                busy = not drop and blob.busy_single.get(kind, 0) > 0
                if drop:
                    blob.drop_single[kind] -= 1
                if busy:
                    blob.busy_single[kind] -= 1
            if drop:
                self.close_connection = True
            if busy:
                self.answer(503, b"busy")
            return drop or busy

        def do_HEAD(self):
            if self.single("HEAD"):
                return
            if blob.head_status is not None:
                self.answer(blob.head_status)
            elif blob.committed is None:
                self.answer(404)
            else:
                self.send_response(200)
                self.send_header("Content-Length", str(len(blob.committed)))
                self.end_headers()

        def do_GET(self):
            if self.single("GET"):
                return
            q = self.query()
            if q.get("comp") != "blocklist" or not blob.staged:
                self.answer(404)
                return
            xml = "<BlockList><UncommittedBlocks>" + "".join(
                f"<Block><Name>{n}</Name><Size>{len(d)}</Size></Block>" for n, d in blob.staged.items()) + "</UncommittedBlocks></BlockList>"
            self.answer(200, xml.encode())

        def do_PUT(self):
            q = self.query()
            data = self.rfile.read(int(self.headers["Content-Length"]))
            if q.get("comp") == "block":
                bid = q["blockid"]
                with blob.lock:
                    blob.block_puts += 1
                    drop = bid in blob.drop_once
                    refuse = bid in blob.refuse_once
                    blob.drop_once.discard(bid); blob.refuse_once.discard(bid)
                if drop:
                    self.close_connection = True      # the body is read and no answer comes: the client sees the line die
                    return
                if refuse:
                    self.answer(500, b"busy")
                    return
                if base64.b64encode(hashlib.md5(data).digest()).decode() != self.headers.get("Content-MD5"):
                    self.answer(400, b"md5")
                    return
                with blob.lock:
                    blob.staged[bid] = data
                self.answer(201, headers={"Connection": "close"} if bid in blob.close_after else None)
            elif q.get("comp") == "blocklist":
                if self.single("COMMIT"):
                    return
                # The service's rule for <Latest>: a block is looked for among the uncommitted ones first and then
                # among those of the committed blob, so the same list committed twice leaves the same blob.
                ids = [e.text for e in ET.fromstring(data).iter("Latest")]
                with blob.lock:
                    have = {**blob.kept, **blob.staged}
                    known = all(i in have for i in ids)
                    if known:
                        blob.kept = {i: have[i] for i in ids}
                        blob.committed = b"".join(have[i] for i in ids)
                        blob.staged = {}
                    lose = known and blob.lose_commit_answer > 0
                    if lose:
                        blob.lose_commit_answer -= 1
                if lose:
                    self.close_connection = True
                elif known:
                    self.answer(201)
                else:
                    self.answer(400, b"InvalidBlockList")
            else:
                self.answer(400)

    return Handler


class Uploads(unittest.TestCase):
    def setUp(self):
        self.blob = Blob()
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(self.blob))
        self.server.daemon_threads = True
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.addCleanup(self.server.server_close)
        self.addCleanup(self.server.shutdown)
        self.sas = os.path.join(self.dir.name, "sas.txt")
        with open(self.sas, "w", encoding="ascii") as f:
            f.write("sv=test&sig=none")
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}/container/folder/file.bin"
        self.pause, B.RETRY_PAUSE = B.RETRY_PAUSE, 0.0
        self.addCleanup(setattr, B, "RETRY_PAUSE", self.pause)

    def file_of(self, blocks, last=700):
        """A file of <blocks> whole 1 MiB blocks and a short last one, every block different."""
        path = os.path.join(self.dir.name, "file.bin")
        data = b"".join(bytes([i + 1]) * (1024 * 1024) for i in range(blocks)) + b"\xfe" * last
        with open(path, "wb") as f:
            f.write(data)
        return path, data

    def upload(self, path, workers):
        return B.main([path, self.url, self.sas, "--workers", str(workers), "--block-mib", "1"])

    def put_block(self, sender, meter, i, data, say):
        """One block through send_block, as main() sends it."""
        target = f"{urllib.parse.urlsplit(self.url).path}?sv=test&comp=block&blockid={urllib.parse.quote(B.block_id(i))}"
        md5 = base64.b64encode(hashlib.md5(data).digest()).decode()
        return B.send_block(sender, meter, target, data, {"Content-Length": str(len(data)), "Content-MD5": md5}, say)

    def test_a_run_with_a_cap_file_reads_it_and_sends_the_file_whole(self):
        path, data = self.file_of(3)
        cap = os.path.join(self.dir.name, "upload-cap-mbps.txt")
        with open(cap, "w", encoding="ascii") as f:
            f.write("4000")
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(B.main([path, self.url, self.sas, "--workers", "2", "--block-mib", "1", "--cap-mbps", "8",
                                     "--cap-file", cap]), 0)
        self.assertEqual(self.blob.committed, data)
        # the effect, not the speed: the file's 4000 reached the Throttle that main() built
        self.assertIn(f"cap now 4000 Mb/s (from {cap})", out.getvalue())

    def test_the_blob_is_the_file_and_a_worker_keeps_its_connection(self):
        path, data = self.file_of(12)
        before = self.blob.connections
        self.assertEqual(self.upload(path, 2), 0)
        self.assertEqual(self.blob.committed, data)
        self.assertEqual(self.blob.block_puts, 13)
        # two workers' two connections, and one each for the four single calls (head, block list, commit, head)
        self.assertLessEqual(self.blob.connections - before, 2 + 4)
        self.assertEqual(self.blob.single_calls, 4)       # a 404 is an answer, not a failure to try again

    def test_a_dropped_connection_is_opened_again_and_the_block_sent_again(self):
        path, data = self.file_of(6)
        self.blob.drop_once = {B.block_id(2), B.block_id(4)}
        before = self.blob.connections
        self.assertEqual(self.upload(path, 1), 0)
        self.assertEqual(self.blob.committed, data)
        self.assertEqual(self.blob.block_puts, 7 + 2)
        # the worker's first connection, one more after each drop, and the four single calls
        self.assertEqual(self.blob.connections - before, 1 + 2 + 4)

    def test_an_error_status_closes_the_connection_and_is_tried_again_on_a_new_one(self):
        path, data = self.file_of(3)
        self.blob.refuse_once = {B.block_id(1)}
        before = self.blob.connections
        self.assertEqual(self.upload(path, 1), 0)
        self.assertEqual(self.blob.committed, data)
        self.assertEqual(self.blob.block_puts, 4 + 1)
        self.assertEqual(self.blob.connections - before, 1 + 1 + 4)

    def test_a_second_run_sends_only_what_is_missing(self):
        path, data = self.file_of(4)
        self.blob.staged = {B.block_id(i): data[i * 1024 * 1024:(i + 1) * 1024 * 1024] for i in (0, 2)}
        self.assertEqual(self.upload(path, 2), 0)
        self.assertEqual(self.blob.committed, data)
        self.assertEqual(self.blob.block_puts, 3)

    def test_a_connection_the_service_closed_is_opened_again_before_the_charge(self):
        # After an answer that carries Connection: close, http.client would open a new connection by itself inside the
        # next request, after the charge and uncounted. Every charge has to find the connection of its block standing.
        self.blob.close_after = {B.block_id(i) for i in range(3)}
        sender, said, stood = B.Sender(self.url), [], []
        self.addCleanup(sender.close)

        class Meter:
            def wait(self, nbytes):
                stood.append((sender.connects, sender.conn is not None and sender.conn.sock is not None))

        before = self.blob.connections
        for i in range(3):
            self.assertEqual(self.put_block(sender, Meter(), i, bytes([i + 1]) * 4096, said.append), 1)
        self.assertEqual(stood, [(1, True), (2, True), (3, True)])
        self.assertEqual(self.blob.connections - before, 3)
        self.assertEqual(said, [])

    def test_a_connection_that_cannot_be_opened_is_not_charged_and_the_next_try_opens_one(self):
        # The real Sender against a port that is bound and never listens: the connect is refused, nothing was sent.
        dead = socket.socket()
        self.addCleanup(dead.close)
        dead.bind(("127.0.0.1", 0))
        sender, said, charged = B.Sender(f"http://127.0.0.1:{dead.getsockname()[1]}/container/folder/file.bin", timeout=30), [], []
        self.addCleanup(sender.close)

        class Meter:
            def wait(self, nbytes):
                charged.append(nbytes)

        def say(line):
            said.append(line)
            self.assertIsNone(sender.conn)
            self.assertEqual(sender.connects, 0)
            sender.port = self.server.server_address[1]     # the line is back for the second try

        self.assertEqual(self.put_block(sender, Meter(), 0, b"\x07" * 4096, say), 2)
        self.assertEqual(charged, [4096])
        self.assertEqual(len(said), 1)
        self.assertEqual(sender.connects, 1)
        self.assertEqual(self.blob.staged, {B.block_id(0): b"\x07" * 4096})

    def test_each_call_outside_the_blocks_is_tried_again_when_its_connection_dies(self):
        path, data = self.file_of(2)
        self.blob.drop_single = {"HEAD": 1, "GET": 1, "COMMIT": 1}
        self.assertEqual(self.upload(path, 1), 0)
        self.assertEqual(self.blob.committed, data)
        self.assertEqual(self.blob.single_calls, 4 + 3)
        self.assertEqual(self.blob.block_puts, 3)

    def test_each_call_outside_the_blocks_is_tried_again_when_the_service_is_busy(self):
        path, data = self.file_of(2)
        self.blob.busy_single = {"HEAD": 1, "GET": 1, "COMMIT": 1}
        self.assertEqual(self.upload(path, 1), 0)
        self.assertEqual(self.blob.committed, data)
        self.assertEqual(self.blob.single_calls, 4 + 3)
        self.assertEqual(self.blob.block_puts, 3)

    def test_a_commit_whose_answer_was_lost_is_sent_again_and_leaves_the_same_blob(self):
        path, data = self.file_of(2)
        self.blob.lose_commit_answer = 1
        self.assertEqual(self.upload(path, 1), 0)
        self.assertEqual(self.blob.committed, data)
        self.assertEqual(self.blob.single_calls, 4 + 1)

    def test_a_status_that_is_an_answer_is_not_tried_again(self):
        path, _ = self.file_of(1)
        self.blob.head_status = 403
        with self.assertRaises(urllib.error.HTTPError) as raised:
            self.upload(path, 1)
        self.assertEqual(raised.exception.code, 403)
        self.assertEqual(self.blob.single_calls, 1)
        self.assertEqual(self.blob.block_puts, 0)

    def test_a_call_that_fails_five_times_is_an_error(self):
        path, _ = self.file_of(1)
        self.blob.drop_single = {"HEAD": 5}
        with self.assertRaises(OSError):
            self.upload(path, 1)
        self.assertEqual(self.blob.single_calls, 5)
        self.assertEqual(self.blob.block_puts, 0)


class Charges(unittest.TestCase):
    """The cap is charged for a try only once its connection stands."""

    class Meter:
        def __init__(self):
            self.charged = []

        def wait(self, nbytes):
            self.charged.append(nbytes)

    class FailingSender:
        """A connection that cannot be opened <connects> times, then cannot send <sends> times, then works."""

        def __init__(self, connects, sends):
            self.connects_left, self.sends_left, self.open, self.sent = connects, sends, False, 0

        def connect(self):
            if self.open:
                return
            if self.connects_left:
                self.connects_left -= 1
                raise TimeoutError("the connection attempt timed out")
            self.open = True

        def put(self, target, body, headers):
            if self.sends_left:
                self.sends_left -= 1
                self.open = False
                raise ConnectionResetError("the line died")
            self.sent += 1

    def setUp(self):
        self.pause, B.RETRY_PAUSE = B.RETRY_PAUSE, 0.0
        self.addCleanup(setattr, B, "RETRY_PAUSE", self.pause)

    def test_a_failed_connect_is_not_charged(self):
        meter, sender, said = self.Meter(), self.FailingSender(3, 0), []
        tries = B.send_block(sender, meter, "/x", b"12345", {}, said.append)
        self.assertEqual(tries, 4)
        self.assertEqual(meter.charged, [5])
        self.assertEqual(len(said), 3)

    def test_a_send_that_died_is_charged_since_bytes_went_out(self):
        meter, sender = self.Meter(), self.FailingSender(0, 2)
        tries = B.send_block(sender, meter, "/x", b"12345", {}, lambda line: None)
        self.assertEqual(tries, 3)
        self.assertEqual(meter.charged, [5, 5, 5])

    def test_eight_failures_are_an_error(self):
        meter, sender = self.Meter(), self.FailingSender(8, 0)
        with self.assertRaises(TimeoutError):
            B.send_block(sender, meter, "/x", b"12345", {}, lambda line: None)
        self.assertEqual(meter.charged, [])


class LiveCap(unittest.TestCase):
    """The cap read again from its file while a run goes on (2026-11-25), on a fake clock: a block's start is when wait
    returns. 1 MB at 8 Mb/s is 1 s."""

    MB = 1_000_000

    def setUp(self):
        self.now = 0.0
        self.hooks = []
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.file = os.path.join(folder.name, "upload-cap-mbps.txt")
        self.said = []

    def clock(self):
        return self.now

    def sleep(self, seconds):
        self.assertGreater(seconds, 0)
        self.assertLessEqual(seconds, 1.0, "a waiting worker sleeps in slices of at most a second")
        self.now += seconds
        for at, text in list(self.hooks):
            if self.now >= at:
                self.write(text)
                self.hooks.remove((at, text))

    def write(self, text):
        with open(self.file, "w", encoding="ascii") as f:
            f.write(text)

    def throttle(self, cap, cap_file=True):
        return B.Throttle(cap, self.file if cap_file else None, recheck=5.0, slice_s=1.0, clock=self.clock,
                          sleep=self.sleep, say=self.said.append)

    def starts(self, throttle, blocks):
        out = []
        for _ in range(blocks):
            throttle.wait(self.MB)
            out.append(round(self.now, 6))
        return out

    def test_without_a_file_blocks_are_spaced_by_the_cap_as_before(self):
        self.assertEqual(self.starts(self.throttle(8, cap_file=False), 4), [0.0, 1.0, 2.0, 3.0])

    def test_a_lower_cap_in_the_file_holds_the_next_blocks_back_within_the_recheck(self):
        self.write("8")
        t = self.throttle(8)
        self.hooks.append((2.0, "1"))
        s = self.starts(t, 9)
        gaps = [b - a for a, b in zip(s, s[1:])]
        self.assertEqual(gaps[:5], [1.0] * 5, "until the file is read again the old cap holds")
        self.assertTrue(all(abs(g - 8.0) < 1e-6 for g in gaps[6:]), f"1 Mb/s spaces 1 MB 8 s apart: {gaps}")
        self.assertEqual(self.said, [f"  cap now 1 Mb/s (from {self.file})"])

    def test_a_higher_cap_lets_a_waiting_worker_go_without_a_restart(self):
        self.write("1")
        t = self.throttle(1)
        self.assertEqual(self.starts(t, 1), [0.0])
        self.hooks.append((2.0, "8"))
        start = self.starts(t, 1)[0]
        # At 1 Mb/s the second block would start at 8 s. The file says 8 from 2 s and is read at 5 s, when 5/8 of the
        # first block's share has gone; the other 3/8 MB at 8 Mb/s is 0.375 s.
        self.assertAlmostEqual(start, 5.375, places=6)

    def test_a_slip_in_the_file_keeps_the_cap(self):
        for text in ("0", "-3", "fast", ""):
            self.assertIsNone(B.read_cap(self.write(text) or self.file), text)
        self.assertIsNone(B.read_cap(self.file + ".missing"))
        self.write("0")
        t = self.throttle(8)
        # 13 blocks at 8 Mb/s reach 12 s: looks at construction, 5 s and 10 s.
        self.assertEqual(len(self.starts(t, 13)), 13)
        self.assertEqual(len(self.said), 1, "a file that is not taken is said once, not at every look")
        self.assertIn("0 or less is ignored", self.said[0])

    def test_a_slip_repeated_after_a_good_read_is_said_again(self):
        self.write("0")
        t = self.throttle(8)
        self.assertEqual(len(self.said), 1)
        self.write("500")
        self.now = 5.0
        self.starts(t, 1)
        self.assertEqual(t.cap, 500)
        self.write("0")
        self.now = 10.0
        self.starts(t, 1)
        self.assertEqual(t.cap, 500, "the slip keeps the cap")
        self.assertEqual(len(self.said), 3, f"slip, cap now 500, slip again: {self.said}")
        self.assertIn("0 or less is ignored", self.said[2])

    def test_a_bom_is_accepted_and_an_unreadable_file_is_said(self):
        with open(self.file, "wb") as f:
            f.write(b"\xef\xbb\xbf4\r\n")
        self.assertEqual(B.read_cap(self.file), 4.0)
        with open(self.file, "wb") as f:
            f.write("4".encode("utf-16"))
        said = []
        self.assertIsNone(B.read_cap(self.file, said.append))
        self.assertEqual(len(said), 1)
        self.assertIn("is not read", said[0])

    def test_idle_time_earns_nothing(self):
        t = self.throttle(8, cap_file=False)
        self.starts(t, 1)
        self.now += 100.0
        # The first block after the idle starts at once; the second still waits its full share, so the idle earned no
        # burst.
        self.assertEqual(self.starts(t, 2), [100.0, 101.0])


if __name__ == "__main__":
    unittest.main()
