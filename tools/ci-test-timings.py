"""Per-class test times of one CI/CD run, Windows against Linux (#2054).

The Windows legs (test-windows) keep a TRX per shard as an artifact; the Linux legs print each test's time in their
log (--output Detailed). This downloads both for one run of dotnet.yml and prints a markdown table per suite: the
summed time of each class on ubuntu-latest and on windows-latest, the ratio, and the Windows shard that ran it.

    python tools/ci-test-timings.py <run id> [--min-seconds 1] [--work <folder outside the repo>]

A summed time is the sum of the class's test durations, not wall-clock: the suites run four tests at once, so a class
that waits on a starved thread pool shows here as slow even when its own work is not.
"""
import argparse
import collections
import glob
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

REPO = 'SharpAstro/tianwen'
TRX_NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
ANSI = re.compile(r'\x1b\[[0-9;]*m')
LINE = re.compile(r'^\S+Z (passed|failed|skipped) (.+?) \(((?:\d+h )?(?:\d+m )?(?:\d+s )?(?:\d+ms)?)\)\s*$')
SUITES = [
    # suite, Linux job, Windows artifact prefix, namespace prefix to drop
    ('unit', 'test-unit (ubuntu-latest)', 'test-windows-trx-unit-', 'TianWen.Lib.Tests.'),
    ('functional', 'test-functional', 'test-windows-trx-functional-', 'TianWen.Lib.Tests.Functional.'),
]


def gh(*args):
    return subprocess.run(['gh', *args], check=True, capture_output=True).stdout


def class_of(test_name):
    return test_name.split('(')[0].rsplit('.', 1)[0]


def seconds(text):
    units = {'h': 3600, 'm': 60, 's': 1, 'ms': 0.001}
    return sum(int(v) * units[u] for v, u in re.findall(r'(\d+)(h|ms|m|s)', text))


def linux_times(log_text):
    out = collections.defaultdict(lambda: [0.0, 0])
    for line in log_text.splitlines():
        m = LINE.match(ANSI.sub('', line).rstrip())
        if m:
            entry = out[class_of(m.group(2))]
            entry[0] += seconds(m.group(3))
            entry[1] += 1
    return out


def windows_times(trx_paths):
    out = collections.defaultdict(lambda: [0.0, 0, 0, ''])
    for path in trx_paths:
        shard = os.path.basename(os.path.dirname(path))
        root = ET.parse(path).getroot()
        classes = {}
        for unit_test in root.iter(TRX_NS + 'UnitTest'):
            method = unit_test.find(TRX_NS + 'TestMethod')
            classes[unit_test.get('id')] = method.get('className') if method is not None else None
        for result in root.iter(TRX_NS + 'UnitTestResult'):
            h, m, s = result.get('duration', '0:0:0').split(':')
            entry = out[classes.get(result.get('testId')) or class_of(result.get('testName'))]
            entry[0] += int(h) * 3600 + int(m) * 60 + float(s)
            entry[1] += 1
            entry[2] += result.get('outcome') == 'Failed'
            entry[3] = shard
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('run', help='the CI/CD run id')
    parser.add_argument('--min-seconds', type=float, default=1.0, help='leave out a class under this on both OSes')
    parser.add_argument('--work', help='where to download to (a temporary folder unless given)')
    args = parser.parse_args()
    work = args.work or tempfile.mkdtemp(prefix='ci-test-timings-')

    jobs = gh('api', f'repos/{REPO}/actions/runs/{args.run}/jobs', '--paginate',
              '--jq', '.jobs[] | "\\(.id)\\t\\(.name)"').decode().splitlines()
    job_ids = {name: job_id for job_id, name in (j.split('\t', 1) for j in jobs)}
    artifacts = gh('api', f'repos/{REPO}/actions/runs/{args.run}/artifacts', '--paginate',
                   '--jq', '.artifacts[].name').decode().split()

    for suite, linux_job, prefix, namespace in SUITES:
        # A job log carries the console's colour codes, which gh refuses to print without this flag.
        log = gh('api', '--allow-escape-sequences', f'repos/{REPO}/actions/jobs/{job_ids[linux_job]}/logs').decode('utf-8', 'replace')
        for name in (a for a in artifacts if a.startswith(prefix)):
            target = os.path.join(work, name.removeprefix('test-windows-trx-'))
            if not glob.glob(os.path.join(target, '*.trx')):
                gh('run', 'download', args.run, '-R', REPO, '-n', name, '-D', target)
        linux = linux_times(log)
        windows = windows_times(sorted(glob.glob(os.path.join(work, f'{suite}-*', '*.trx'))))

        rows = []
        for cls in set(linux) | set(windows):
            lin = linux.get(cls, [0.0, 0])
            win = windows.get(cls, [0.0, 0, 0, ''])
            rows.append((cls.removeprefix(namespace), lin[0], win[0], lin[1], win[1], win[2], win[3]))
        shown = sorted((r for r in rows if max(r[1], r[2]) >= args.min_seconds), key=lambda r: -max(r[1], r[2]))
        print(f'### {suite}: {len(rows)} classes, Linux {sum(r[1] for r in rows):.0f} s summed, '
              f'Windows {sum(r[2] for r in rows):.0f} s summed ({len(shown)} at {args.min_seconds:g} s or more shown)')
        print()
        print('| class | Linux s | Windows s | W/L | tests L/W | failed on W | Windows shard |')
        print('|---|---:|---:|---:|---:|---:|---|')
        for cls, lin, win, n_lin, n_win, failed, shard in shown:
            ratio = f'{win / lin:.1f}' if lin >= 0.05 else ''
            print(f'| {cls} | {lin:.1f} | {win:.1f} | {ratio} | {n_lin}/{n_win} | {failed or ""} | {shard} |')
        print()
    return 0


if __name__ == '__main__':
    sys.exit(main())
