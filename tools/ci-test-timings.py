"""Per-class and per-test times of one CI/CD run, Linux against Windows, beside a run before it (#2054).

The Windows legs (test-windows) keep a TRX per shard as an artifact; the Linux legs print each test's time in their
log (--output Detailed). This downloads both for one run of dotnet.yml and prints a markdown table per suite: the
summed time of each class on ubuntu-latest and on windows-latest, the ratio, and the Windows shard that ran it. With
--before, a second run's Linux time is set beside it (the run before a change, so the table is a before and after),
and --per-test N adds the N slowest tests the same way.

    python tools/ci-test-timings.py <run id> [--before <run id>] [--per-test 40] [--min-seconds 1] [--work <folder>]

The Linux jobs are read whatever their shape: one job per suite (`test-unit (ubuntu-latest)`, `test-functional`, as
before #2054) or one per shard (`test-unit (ubuntu-latest 3/5)`, `test-functional (1/2)`), summed over the shards.

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
    # suite, Linux job names (unsharded or one shard), Windows artifact prefix, namespace prefix to drop
    ('unit', re.compile(r'^test-unit \(ubuntu-latest(?: \d+/\d+)?\)$'), 'test-windows-trx-unit-', 'TianWen.Lib.Tests.'),
    ('functional', re.compile(r'^test-functional(?: \(\d+/\d+\))?$'), 'test-windows-trx-functional-',
     'TianWen.Lib.Tests.Functional.'),
]


def gh(*args):
    return subprocess.run(['gh', *args], check=True, capture_output=True).stdout


def class_of(test_name):
    return test_name.split('(')[0].rsplit('.', 1)[0]


def seconds(text):
    units = {'h': 3600, 'm': 60, 's': 1, 'ms': 0.001}
    return sum(int(v) * units[u] for v, u in re.findall(r'(\d+)(h|ms|m|s)', text))


def linux_tests(run, job_pattern, work):
    """Each test's time, from every Linux job of the run whose name matches; cached under work."""
    jobs = gh('api', f'repos/{REPO}/actions/runs/{run}/jobs', '--paginate',
              '--jq', '.jobs[] | "\\(.id)\\t\\(.name)"').decode().splitlines()
    out = {}
    for job_id, name in (j.split('\t', 1) for j in jobs):
        if not job_pattern.match(name):
            continue
        cached = os.path.join(work, f'linux-{run}-{job_id}.log')
        if not os.path.exists(cached):
            # A job log carries the console's colour codes, which gh refuses to print without this flag.
            text = gh('api', '--allow-escape-sequences', f'repos/{REPO}/actions/jobs/{job_id}/logs').decode('utf-8', 'replace')
            with open(cached, 'w', encoding='utf-8') as f:
                f.write(text)
        with open(cached, encoding='utf-8') as f:
            for line in f:
                m = LINE.match(ANSI.sub('', line).rstrip())
                if m:
                    out[m.group(2)] = out.get(m.group(2), 0.0) + seconds(m.group(3))
    return out


def windows_tests(run, prefix, work):
    """Each test's time and outcome, and the shard that ran it, from the run's Windows TRX artifacts."""
    artifacts = gh('api', f'repos/{REPO}/actions/runs/{run}/artifacts', '--paginate',
                   '--jq', '.artifacts[].name').decode().split()
    out = {}
    for name in (a for a in artifacts if a.startswith(prefix)):
        target = os.path.join(work, f'{run}-{name.removeprefix("test-windows-trx-")}')
        if not glob.glob(os.path.join(target, '*.trx')):
            gh('run', 'download', str(run), '-R', REPO, '-n', name, '-D', target)
        shard = name.removeprefix('test-windows-trx-')
        for path in glob.glob(os.path.join(target, '*.trx')):
            root = ET.parse(path).getroot()
            classes = {}
            for unit_test in root.iter(TRX_NS + 'UnitTest'):
                method = unit_test.find(TRX_NS + 'TestMethod')
                classes[unit_test.get('id')] = method.get('className') if method is not None else None
            for result in root.iter(TRX_NS + 'UnitTestResult'):
                h, m, s = result.get('duration', '0:0:0').split(':')
                test = result.get('testName')
                cls = classes.get(result.get('testId')) or class_of(test)
                # The TRX names a test by its method; the Linux log by class and method. Key both the Linux way.
                key = test if test.startswith(cls) else f'{cls}.{test}'
                out[key] = (int(h) * 3600 + int(m) * 60 + float(s), result.get('outcome') == 'Failed', shard, cls)
    return out


def by_class(tests, class_for):
    out = collections.defaultdict(lambda: [0.0, 0])
    for test, value in tests.items():
        entry = out[class_for(test, value)]
        entry[0] += value if isinstance(value, float) else value[0]
        entry[1] += 1
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('run', help='the CI/CD run id')
    parser.add_argument('--before', help='a run to set beside it (its Linux times: the before column)')
    parser.add_argument('--per-test', type=int, default=0, help='also list the N slowest tests')
    parser.add_argument('--min-seconds', type=float, default=1.0, help='leave out a class under this everywhere')
    parser.add_argument('--work', help='where to download to (a temporary folder unless given)')
    args = parser.parse_args()
    work = args.work or tempfile.mkdtemp(prefix='ci-test-timings-')
    os.makedirs(work, exist_ok=True)

    for suite, linux_job, prefix, namespace in SUITES:
        lin_tests = linux_tests(args.run, linux_job, work)
        win_tests = windows_tests(args.run, prefix, work)
        before_tests = linux_tests(args.before, linux_job, work) if args.before else {}
        lin = by_class(lin_tests, lambda t, v: class_of(t))
        before = by_class(before_tests, lambda t, v: class_of(t))
        win = by_class(win_tests, lambda t, v: v[3])
        win_failed = collections.Counter(v[3] for v in win_tests.values() if v[1])
        win_shard = {v[3]: v[2] for v in win_tests.values()}

        rows = []
        for cls in set(lin) | set(win) | set(before):
            b, l, w = before.get(cls, [0.0, 0]), lin.get(cls, [0.0, 0]), win.get(cls, [0.0, 0])
            rows.append((cls.removeprefix(namespace), b[0], l[0], w[0], b[1], l[1], w[1], win_failed[cls],
                         win_shard.get(cls, '')))
        shown = sorted((r for r in rows if max(r[1], r[2], r[3]) >= args.min_seconds), key=lambda r: -max(r[1:4]))
        totals = f'Linux {sum(r[2] for r in rows):.0f} s summed, Windows {sum(r[3] for r in rows):.0f} s'
        if args.before:
            totals = f'Linux before {sum(r[1] for r in rows):.0f} s summed, ' + totals
        print(f'### {suite}: {len(rows)} classes, {totals} ({len(shown)} at {args.min_seconds:g} s or more shown)')
        print()
        if args.before and not before_tests:
            print(f'(run {args.before} printed no per-test times for this suite, so its before column is empty)')
            print()
        b_head, b_rule = ('Linux before s | ', '---:|') if args.before else ('', '')
        print(f'| class | {b_head}Linux s | Windows s | W/L | tests L/W | failed on W | Windows shard |')
        print(f'|---|{b_rule}---:|---:|---:|---:|---:|---|')
        for cls, b, l, w, n_b, n_l, n_w, failed, shard in shown:
            ratio = f'{w / l:.1f}' if l >= 0.05 else ''
            b_cell = f'{b:.1f} | ' if args.before else ''
            print(f'| {cls} | {b_cell}{l:.1f} | {w:.1f} | {ratio} | {n_l}/{n_w} | {failed or ""} | {shard} |')
        print()

        if args.per_test:
            keys = set(lin_tests) | set(win_tests) | set(before_tests)
            slowest = sorted(keys, key=lambda k: -max(lin_tests.get(k, 0.0), before_tests.get(k, 0.0),
                                                      win_tests.get(k, (0.0,))[0]))[:args.per_test]
            print(f'#### {suite}: the {len(slowest)} slowest tests')
            print()
            print(f'| test | {b_head}Linux s | Windows s |')
            print(f'|---|{b_rule}---:|---:|')
            for k in slowest:
                b_cell = f'{before_tests[k]:.1f} | ' if k in before_tests else (' | ' if args.before else '')
                l_cell = f'{lin_tests[k]:.1f}' if k in lin_tests else ''
                w_cell = f'{win_tests[k][0]:.1f}' if k in win_tests else ''
                print(f'| {k.removeprefix(namespace)} | {b_cell}{l_cell} | {w_cell} |')
            print()
    return 0


if __name__ == '__main__':
    sys.exit(main())
