"""Ranks what each test cost the test process, from the file TIANWEN_TEST_COST_FILE names (TestCostProbe.cs).

    python tools/test-cost-report.py <cost file> [--top 30] [--per-test 30] [--strip TianWen.Lib.Tests.]

A line of the file is a test's display name, its wall time in ms, the bytes allocated, the gen0, gen1 and gen2
collections and the ms the collector held every thread, all read off the PROCESS's counters. So the numbers are one
test's only when the run was serial (`dotnet test ... --parallel none`); in a parallel run a line also holds what the
tests beside it did.

The probe appends to its file. A name that repeats is either a second run appended to the file or
rows of one theory whose display names xunit shortened to the same text (a string argument is cut at 50 characters),
so the script cannot tell which. Both tables SUM the lines (the tests column counts lines), and the first line of the
output says how many names repeat. If a second run was appended, name a new file for a run, or delete the old one, to
read one run.

Prints a markdown table of the classes by bytes allocated, with each one's share of the run's wall time spent in
collector pauses, and with --per-test the single tests the same way.
"""
import argparse
import collections


def class_of(test):
    return test.split('(')[0].rsplit('.', 1)[0]


def gib(n):
    return n / 2 ** 30


def table(title, rows, top, name_label):
    print(f"### {title}\n")
    print(f"| {name_label} | tests | wall s | allocated GiB | gen0 | gen1 | gen2 | pause s | pause % |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|")
    for name, r in sorted(rows.items(), key=lambda kv: -kv[1][2])[:top]:
        count, wall, alloc, g0, g1, g2, pause = r
        share = 100 * pause / wall if wall else 0
        print(f"| {name} | {count} | {wall / 1000:.1f} | {gib(alloc):.2f} | {g0} | {g1} | {g2} | {pause / 1000:.1f} | {share:.0f} |")
    print()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('cost_file')
    ap.add_argument('--top', type=int, default=30, help='classes shown')
    ap.add_argument('--per-test', type=int, default=0, help='single tests shown')
    ap.add_argument('--strip', default='TianWen.Lib.Tests.', help='a prefix dropped from every name')
    args = ap.parse_args()

    classes = collections.defaultdict(lambda: [0, 0, 0, 0, 0, 0, 0])
    tests = {}
    total = [0, 0, 0, 0, 0, 0, 0]
    with open(args.cost_file, encoding='utf-8') as f:
        for line in f:
            parts = line.rstrip('\n').split('\t')
            if len(parts) != 7:
                continue
            name = parts[0][len(args.strip):] if parts[0].startswith(args.strip) else parts[0]
            nums = [int(p) for p in parts[1:]]
            row = classes[class_of(name)]
            row[0] += 1
            total[0] += 1
            for i, v in enumerate(nums, 1):
                row[i] += v
                total[i] += v
            # A name seen again is a second run appended to the same file: summed here as the class's row is, so the
            # two tables agree, and counted so the note can say so.
            test_row = tests.setdefault(name, [0, 0, 0, 0, 0, 0, 0])
            test_row[0] += 1
            for i, v in enumerate(nums, 1):
                test_row[i] += v

    repeated = sum(1 for row in tests.values() if row[0] > 1)
    if repeated:
        print(f"NOTE: {repeated} of {len(tests)} test names appear on more than one line: either a second run was "
              f"appended to the file, or rows of one theory share a shortened display name. Every figure below is "
              f"the sum of its lines.\n")
    count, wall, alloc, g0, g1, g2, pause = total
    print(f"{count} lines in {len(classes)} classes: {wall / 1000:.0f} s inside tests, {gib(alloc):.1f} GiB allocated, "
          f"{g0}/{g1}/{g2} collections (gen0/1/2), {pause / 1000:.0f} s of collector pause "
          f"({100 * pause / wall if wall else 0:.0f}% of the time inside tests)\n")
    table(f"The {min(args.top, len(classes))} classes that allocate most", classes, args.top, 'class')
    if args.per_test:
        table(f"The {min(args.per_test, len(tests))} tests that allocate most", tests, args.per_test, 'test')


if __name__ == '__main__':
    main()
