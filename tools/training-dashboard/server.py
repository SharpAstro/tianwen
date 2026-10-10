"""The training dashboard: a local page over every long run a registry names (README.md beside this file).

python server.py [--runs C:/temp/e2/dashboard/runs.json] [--explain explain.json] [--port 8765]
then open http://127.0.0.1:8765

Read-only: it looks at processes (psutil), the GPU (nvidia-smi), status files, the TAILS of console logs (opened shared,
never locked, the last 16 KiB only) and the starless-eval reports. It starts, stops and writes nothing but its own pid
file, beside the registry. The registry and the glossary are re-read on every refresh, so a run is added by adding an
entry.
"""
import argparse
import glob
import json
import os
import re
import shutil
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import psutil

HERE = os.path.dirname(os.path.abspath(__file__))
TAIL_BYTES = 16384
RUNS = os.path.join(HERE, 'runs.json')         # set from --runs
EXPLAIN = os.path.join(HERE, 'explain.json')   # set from --explain

_procs = {}  # pid -> psutil.Process, kept so cpu_percent reads the time since the last refresh
_lock = threading.Lock()


def tail(path, n=TAIL_BYTES):
    """The last n bytes of a file as text, or None. Read-only and shared: a writer appending to it is never blocked."""
    try:
        with open(path, 'rb') as f:
            f.seek(0, os.SEEK_END)
            size = f.tell()
            f.seek(max(0, size - n))
            return f.read().decode('utf-8', errors='replace')
    except OSError:
        return None


def read_small(path, limit=4096):
    try:
        with open(path, 'rb') as f:
            return f.read(limit).decode('utf-8', errors='replace').strip()
    except OSError:
        return None


def mtime(path):
    try:
        return os.path.getmtime(path)
    except OSError:
        return None


def proc_stats(p, with_children=True):
    """CPU (percent of one core) and memory (GB) of a process, its children's added unless with_children is False, and
    its command line."""
    with _lock:
        cached = _procs.get(p.pid)
        if cached is None or cached.create_time() != p.create_time():
            _procs[p.pid] = cached = p
            cached.cpu_percent(None)
    try:
        family = [cached] + (cached.children(recursive=True) if with_children else [])
        cpu = 0.0
        mem = 0
        for q in family:
            with _lock:
                known = _procs.setdefault(q.pid, q)
            try:
                cpu += known.cpu_percent(None)
                mem += known.memory_info().rss
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
        return {'pid': cached.pid, 'name': cached.name(), 'cpu': round(cpu, 1), 'memGb': round(mem / 2**30, 2),
                'started': cached.create_time(), 'cmd': ' '.join(cached.cmdline())[:400], 'children': len(family) - 1}
    except (psutil.NoSuchProcess, psutil.AccessDenied):
        return None


def find_process(run):
    if 'pid' in run:
        try:
            return psutil.Process(run['pid'])
        except psutil.NoSuchProcess:
            return None
    match = run.get('match')
    if not match:
        return None
    exe = (run.get('exe') or '').lower()
    best = None
    for p in psutil.process_iter(['pid', 'name', 'cmdline', 'create_time']):
        try:
            cmd = ' '.join(p.info['cmdline'] or []).replace('\\', '/')
            name = (p.info['name'] or '').lower()
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
        if exe and not name.startswith(exe):
            continue
        if all(m in cmd for m in match) and 'server.py' not in cmd:
            if best is None or p.info['create_time'] > best.info['create_time']:
                best = p
    return best


STEP = re.compile(r'step\s+(\d+)\s*/\s*(\d+).*?([\d.]+)\s*tiles/s.*?elapsed\s+([\d.]+)\s*min')
BEST = re.compile(r'best\s+([\d.eE+-]+)\s+at\s+(\d+)\s+lr\s+([\d.eE+-]+)')


def progress(run, log_tail):
    spec = run.get('progress') or {}
    kind = spec.get('type')
    out = {'type': kind}
    if kind == 'steps' and log_tail:
        steps = STEP.findall(log_tail)
        if steps:
            step, total, rate, elapsed = steps[-1]
            step, total, rate, elapsed = int(step), int(total), float(rate), float(elapsed)
            out.update(done=step, total=total, rate=f'{rate:g} tiles/s', elapsedMin=elapsed)
            per_step_min = elapsed / step if step else None
            out['etaCapMin'] = round((total - step) * per_step_min) if per_step_min else None
        best = BEST.findall(log_tail)
        if best:
            value, at, lr = best[-1]
            out['best'] = f'{float(value):.4g} at {at}'
            out['lr'] = lr
    elif kind == 'count' and log_tail is not None:
        pattern = re.compile(spec['pattern'], re.MULTILINE)
        full = tail(run['log'], 4 * 1024 * 1024) or ''
        out.update(done=len(pattern.findall(full)), total=spec.get('total'))
    elif kind == 'files':
        out.update(done=len(glob.glob(spec['glob'])), total=spec.get('total'))
    if log_tail:
        lines = [l for l in log_tail.splitlines() if l.strip()]
        out['last'] = lines[-1][-300:] if lines else ''
    return out


def drive_free(path):
    try:
        root = os.path.splitdrive(os.path.abspath(path))[0] + '/'
        u = shutil.disk_usage(root)
        return {'drive': root, 'freeGb': round(u.free / 2**30), 'totalGb': round(u.total / 2**30)}
    except OSError:
        return None


def run_status(run):
    p = find_process(run)
    stats = proc_stats(p) if p else None
    log_tail = tail(run['log']) if run.get('log') else None
    status_text = read_small(run['status']) if run.get('status') else None
    stop = run.get('stop')
    stores = []
    for path in run.get('datastore', []):
        stores.append({'path': path, 'exists': os.path.exists(path), 'free': drive_free(path)})
    result = run.get('result')
    prog = progress(run, log_tail)
    if stats:
        state = 'running'
    elif status_text and re.match(r'(done|failed|stopped)', status_text):
        state = status_text.split()[0]
    elif (result and os.path.exists(result)) or (prog.get('total') and (prog.get('done') or 0) >= prog['total']):
        state = 'done'
    else:
        state = 'waiting'
    return {
        'tag': run['tag'], 'title': run.get('title') or run['tag'], 'kind': run.get('kind'), 'what': run.get('what'),
        'question': run.get('question'), 'setup': run.get('setup'), 'predictions': run.get('predictions') or [],
        'kill': run.get('kill'), 'caveat': run.get('caveat'), 'state': state, 'process': stats,
        'status': status_text, 'statusAge': time.time() - mtime(run['status']) if run.get('status') and mtime(run['status']) else None,
        'logAge': time.time() - mtime(run['log']) if run.get('log') and mtime(run['log']) else None,
        'stopFile': stop, 'stopRequested': bool(stop and os.path.exists(stop)),
        'progress': prog, 'datastores': stores,
        'result': result, 'resultReady': bool(result and os.path.exists(result)),
    }


def gpu():
    try:
        out = subprocess.run(['nvidia-smi', '--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw',
                              '--format=csv,noheader,nounits'], capture_output=True, text=True, timeout=5).stdout.strip()
        name, util, used, total, temp, power = [x.strip() for x in out.splitlines()[0].split(',')]
        return {'name': name, 'util': float(util), 'memUsedGb': round(float(used) / 1024, 1), 'memTotalGb': round(float(total) / 1024, 1),
                'tempC': float(temp), 'powerW': float(power)}
    except Exception as e:  # nvidia-smi absent or busy: the rest of the page still serves
        return {'error': str(e)}


def describe(model, glossary):
    for entry in glossary:
        if re.search(entry['match'], model):
            return entry['what']
    return ''


def starless_scores(spec, glossary):
    rows = []
    for path in sorted(glob.glob(spec['glob'])):
        try:
            d = json.load(open(path, encoding='utf-8'))
            o = next(a for a in d['Arms'] if a['Name'] == 'output')
            b = {c['Band']: c for c in o['Completeness']}
            parts = os.path.normpath(path).split(os.sep)
            model = parts[-3] if parts[-2] == 'on-random' else parts[-2]
            rows.append({
                'model': model, 'what': describe(model, glossary), 'when': mtime(path),
                'clean5': 100 * b['5-20 sigma']['CleanRate'], 'clean20': 100 * b['20-100 sigma']['CleanRate'],
                'clean100': 100 * b['100-1000 sigma']['CleanRate'], 'dug100': 100 * b['100-1000 sigma']['DugRate'],
                'cleanSat': 100 * b['saturated']['CleanRate'], 'brightCore': 100 * o['Loss']['BrightCores'],
                'farSky': o['PlateSources']['FarRms'],
            })
        except Exception:
            continue
    rows.sort(key=lambda r: r['when'] or 0, reverse=True)
    return {'name': spec['name'], 'rows': rows}


def family_of(pid):
    """A run's process, every child and every parent that launched it: what the other-processes table leaves out."""
    try:
        p = psutil.Process(pid)
        return {pid} | {c.pid for c in p.children(recursive=True)} | {q.pid for q in p.parents()}
    except psutil.NoSuchProcess:
        return {pid}


def others(names, known_pids):
    out = []
    for p in psutil.process_iter(['pid', 'name']):
        try:
            name = (p.info['name'] or '').lower()
            if p.info['pid'] in known_pids or not any(name.startswith(n) for n in names):
                continue
            s = proc_stats(p, with_children=False)
            if s and (s['cpu'] > 1 or s['memGb'] > 0.5):
                out.append(s)
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    out.sort(key=lambda s: -s['memGb'])
    return out


def drives():
    """Every fixed drive's free space; a drive that will not answer (an absent USB disk) is left out."""
    out = []
    for part in psutil.disk_partitions(all=False):
        if 'cdrom' in part.opts or not part.fstype:
            continue
        d = drive_free(part.mountpoint)
        if d:
            out.append(d)
    return out


def status():
    cfg = json.load(open(RUNS, encoding='utf-8'))
    try:
        explain = json.load(open(EXPLAIN, encoding='utf-8'))
    except (OSError, ValueError):
        explain = {}
    runs = [run_status(r) for r in cfg.get('runs', [])]
    known = set()
    for r in runs:
        if r['process']:
            known |= family_of(r['process']['pid'])
    vm = psutil.virtual_memory()
    return {
        'now': time.time(),
        'system': {'cpu': psutil.cpu_percent(None), 'cores': psutil.cpu_count(), 'memUsedGb': round((vm.total - vm.available) / 2**30, 1),
                   'memTotalGb': round(vm.total / 2**30, 1),
                   'drives': drives()},
        'gpu': gpu(),
        'runs': runs,
        'others': others(cfg.get('watch', []), known),
        'scores': [starless_scores(s, explain.get('models', [])) for s in cfg.get('scores', [])],
        'programme': explain.get('programme'),
        'metrics': explain.get('metrics', {}),
    }


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith('/api/status'):
            body = json.dumps(status()).encode('utf-8')
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
        elif self.path in ('/', '/index.html'):
            body = open(os.path.join(HERE, 'index.html'), 'rb').read()
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
        else:
            self.send_response(404)
            self.end_headers()
            return
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
            pass  # the page was closed or refreshed mid-reply

    def log_message(self, *args):
        pass


if __name__ == '__main__':
    ap = argparse.ArgumentParser(description='The training dashboard (README.md beside this file).')
    ap.add_argument('--runs', default=RUNS, help='the run registry (default: runs.json beside this file)')
    ap.add_argument('--explain', default=EXPLAIN, help='the glossary the page explains runs and scores with')
    ap.add_argument('--port', type=int, default=8765)
    args = ap.parse_args()
    RUNS, EXPLAIN = os.path.abspath(args.runs), os.path.abspath(args.explain)
    if not os.path.exists(RUNS):
        raise SystemExit(f'no registry at {RUNS}: copy runs.example.json there, or pass --runs')
    psutil.cpu_percent(None)
    with open(os.path.join(os.path.dirname(RUNS), 'server.pid'), 'w') as f:
        f.write(str(os.getpid()))
    print(f'training dashboard on http://127.0.0.1:{args.port}', flush=True)
    ThreadingHTTPServer(('127.0.0.1', args.port), Handler).serve_forever()
