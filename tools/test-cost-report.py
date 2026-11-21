"""Ranks what each test cost the test process, from the file TIANWEN_TEST_COST_FILE names (TestCostProbe.cs).

    python tools/test-cost-report.py <cost file> [--top 30] [--per-test 30] [--strip TianWen.Lib.Tests.]

A line of the file is a test's display name, its wall time in ms, the bytes allocated, the gen0, gen1 and gen2
collections and the ms the collector held every thread, all read off the PROCESS's counters. So the numbers are one
test's only when the run was serial (`dotnet test ... --parallel none`); in a parallel run a line also holds what the
tests beside it did.

A run with TIANWEN_TEST_COST_PEAKS=1 has three more fields a line, in bytes: the managed heap as the test began (the
heap collected first), the highest heap while it ran and the highest working set. Two more tables are then printed,
the classes and the tests by what they ADDED to the heap (peak less start): a class's row is its hungriest test's,
beside the highest working set of any of its tests.

The probe appends to its file. A file that holds a test more than once holds more than one run: the allocation tables
then SUM the runs (the tests column counts the lines) and the memory tables keep each test's hungriest run, and the
first line of the output says how many tests are repeated. Name a new file for a run, or delete the old one, to read
one run.

Prints a markdown table of the classes by bytes allocated, with each one's share of the run's wall time spent in
collector pauses, and with --per-test the single tests the same way.
"""
import argparse
import collections


def class_of(test):
    return test.split('(')[0].rsplit('.', 1)[0]


def gib(n):
    return n / 2 ** 30


def mib(n):
    return n / 2 ** 20


def table(title, rows, top, name_label):
    print(f"### {title}\n")
    print(f"| {name_label} | tests | wall s | allocated GiB | gen0 | gen1 | gen2 | pause s | pause % |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|")
    for name, r in sorted(rows.items(), key=lambda kv: -kv[1][2])[:top]:
        count, wall, alloc, g0, g1, g2, pause = r
        share = 100 * pause / wall if wall else 0
        print(f"| {name} | {count} | {wall / 1000:.1f} | {gib(alloc):.2f} | {g0} | {g1} | {g2} | {pause / 1000:.1f} | {share:.0f} |")
    print()


def memory_table(title, rows, top, name_label):
    """rows: name -> [heap added, heap at start, heap peak, working set peak], in bytes."""
    print(f"### {title}\n")
    print(f"| {name_label} | heap at start MiB | heap added MiB | heap peak MiB | working set peak MiB |")
    print("|---|---:|---:|---:|---:|")
    for name, (added, start, peak, working_set) in sorted(rows.items(), key=lambda kv: -kv[1][0])[:top]:
        print(f"| {name} | {mib(start):.0f} | {mib(added):.0f} | {mib(peak):.0f} | {mib(working_set):.0f} |")
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
    # Memory, only from lines that carry the three peak fields: name -> [added, start, peak, working set].
    test_memory = {}
    class_memory = {}
    with open(args.cost_file, encoding='utf-8') as f:
        for line in f:
            parts = line.rstrip('\n').split('\t')
            if len(parts) not in (7, 10):
                continue
            name = parts[0][len(args.strip):] if parts[0].startswith(args.strip) else parts[0]
            nums = [int(p) for p in parts[1:7]]
            row = classes[class_of(name)]
            row[0] += 1
            total[0] += 1
            for i, v in enumerate(nums, 1):
                row[i] += v
                total[i] += v
            # A name seen again is a second run appended to the same file: summed here as the class's row is, so the
            # two tables agree, and counted so the header can say so.
            test_row = tests.setdefault(name, [0, 0, 0, 0, 0, 0, 0])
            test_row[0] += 1
            for i, v in enumerate(nums, 1):
                test_row[i] += v
            if len(parts) == 10:
                start, peak, working_set = (int(p) for p in parts[7:10])
                seen = [peak - start, start, peak, working_set]
                # A peak is not summed over runs: a test's row is its hungriest run.
                if name not in test_memory or seen[0] > test_memory[name][0]:
                    test_memory[name] = seen
                # A class's row is its hungriest test's, beside the highest working set any of its tests saw.
                of_class = class_memory.get(class_of(name))
                if of_class is None:
                    class_memory[class_of(name)] = list(seen)
                else:
                    highest_working_set = max(of_class[3], working_set)
                    if seen[0] > of_class[0]:
                        of_class[:3] = seen[:3]
                    of_class[3] = highest_working_set

    repeated = sum(1 for row in tests.values() if row[0] > 1)
    if repeated:
        print(f"NOTE: {repeated} of {len(tests)} tests appear more than once: the file holds more than one run, "
              f"and every allocation figure below is their sum.\n")
    count, wall, alloc, g0, g1, g2, pause = total
    print(f"{count} tests in {len(classes)} classes: {wall / 1000:.0f} s inside tests, {gib(alloc):.1f} GiB allocated, "
          f"{g0}/{g1}/{g2} collections (gen0/1/2), {pause / 1000:.0f} s of collector pause "
          f"({100 * pause / wall if wall else 0:.0f}% of the time inside tests)\n")
    if test_memory:
        hungriest = max(test_memory.items(), key=lambda kv: kv[1][0])
        largest = max(test_memory.items(), key=lambda kv: kv[1][3])
        print(f"Memory, from the {len(test_memory)} tests whose lines carry peaks: the most any test added to the heap "
              f"is {mib(hungriest[1][0]):.0f} MiB ({hungriest[0]}), and the process's working set peaked at "
              f"{mib(largest[1][3]):.0f} MiB ({largest[0]})\n")
    table(f"The {min(args.top, len(classes))} classes that allocate most", classes, args.top, 'class')
    if args.per_test:
        table(f"The {min(args.per_test, len(tests))} tests that allocate most", tests, args.per_test, 'test')
    if test_memory:
        memory_table(f"The {min(args.top, len(class_memory))} classes by what one test added to the heap",
                     class_memory, args.top, 'class')
        if args.per_test:
            memory_table(f"The {min(args.per_test, len(test_memory))} tests that added most to the heap",
                         test_memory, args.per_test, 'test')


if __name__ == '__main__':
    main()
