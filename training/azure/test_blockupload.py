"""blockupload.py against a local stand-in for the blob service: connections are kept, a dropped one is opened again,
and a try that fails while connecting is not charged to the cap (#2300).

    cd training/azure && python -m unittest test_blockupload -v
"""
import base64, hashlib, os, tempfile, threading, unittest, urllib.parse
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import blockupload as B


class Blob:
    """What the stand-in service holds for its one blob, and what it saw."""

    def __init__(self):
        self.lock = threading.Lock()
        self.staged = {}          # block id -> bytes
        self.committed = None     # bytes once Put Block List came
        self.connections = 0
        self.block_puts = 0
        self.drop_once = set()    # block ids whose first Put Block is read and then dropped without an answer
        self.refuse_once = set()  # block ids whose first Put Block is answered 500


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

        def do_HEAD(self):
            if blob.committed is None:
                self.answer(404)
            else:
                self.send_response(200)
                self.send_header("Content-Length", str(len(blob.committed)))
                self.end_headers()

        def do_GET(self):
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
                self.answer(201)
            elif q.get("comp") == "blocklist":
                ids = [e.text for e in ET.fromstring(data).iter("Latest")]
                with blob.lock:
                    blob.committed = b"".join(blob.staged[i] for i in ids)
                    blob.staged = {}
                self.answer(201)
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

    def test_the_blob_is_the_file_and_a_worker_keeps_its_connection(self):
        path, data = self.file_of(12)
        before = self.blob.connections
        self.assertEqual(self.upload(path, 2), 0)
        self.assertEqual(self.blob.committed, data)
        self.assertEqual(self.blob.block_puts, 13)
        # two workers' two connections, and one each for the four single calls (head, block list, commit, head)
        self.assertLessEqual(self.blob.connections - before, 2 + 4)

    def test_a_dropped_connection_is_opened_again_and_the_block_sent_again(self):
        path, data = self.file_of(6)
        self.blob.drop_once = {B.block_id(2), B.block_id(4)}
        self.assertEqual(self.upload(path, 1), 0)
        self.assertEqual(self.blob.committed, data)
        self.assertEqual(self.blob.block_puts, 7 + 2)

    def test_an_error_status_is_tried_again(self):
        path, data = self.file_of(3)
        self.blob.refuse_once = {B.block_id(1)}
        self.assertEqual(self.upload(path, 1), 0)
        self.assertEqual(self.blob.committed, data)
        self.assertEqual(self.blob.block_puts, 4 + 1)

    def test_a_second_run_sends_only_what_is_missing(self):
        path, data = self.file_of(4)
        self.blob.staged = {B.block_id(i): data[i * 1024 * 1024:(i + 1) * 1024 * 1024] for i in (0, 2)}
        self.assertEqual(self.upload(path, 2), 0)
        self.assertEqual(self.blob.committed, data)
        self.assertEqual(self.blob.block_puts, 3)


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


if __name__ == "__main__":
    unittest.main()
