#!/usr/bin/env python3
"""Rank Plank classes by how little of them a fuzzing corpus reached.

Cobertura XML is per-line, which is the right granularity to fuzz against but
useless to read: the file is megabytes of <line> elements. What decides where
to aim next is which *classes* are cold, and in particular which are at a flat
zero — a decoder at 0% is not being reached at all, which is a corpus or a
target-driver gap rather than a hard-to-hit branch.

Usage: coverage-report.py <cobertura.xml> [--all]
"""
import collections
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__, file=sys.stderr)
        return 2
    path = sys.argv[1]
    show_all = "--all" in sys.argv[2:]

    tree = ET.parse(path)

    # A class can appear once per source file it spans (partials, and coverlet
    # emits one <class> per method group), so accumulate by name.
    hits = collections.Counter()
    total = collections.Counter()
    for cls in tree.iter("class"):
        name = cls.get("name", "?")
        # Compiler-generated closures and iterators are noise: they are covered
        # exactly when their enclosing method is.
        base = name.split("/")[0]
        for line in cls.iter("line"):
            total[base] += 1
            if int(line.get("hits", "0")) > 0:
                hits[base] += 1

    if not total:
        print("no classes in report", file=sys.stderr)
        return 1

    rows = []
    for name, n in total.items():
        covered = hits[name]
        rows.append((covered / n, covered, n, name))
    rows.sort(key=lambda r: (r[0], -r[2]))

    grand_hit = sum(hits.values())
    grand_total = sum(total.values())
    print(f"overall: {grand_hit}/{grand_total} lines = {grand_hit / grand_total:.1%} "
          f"across {len(total)} classes")

    zero = [r for r in rows if r[1] == 0]
    print(f"never executed: {len(zero)} classes, {sum(r[2] for r in zero)} lines")
    print()

    shown = rows if show_all else [r for r in rows if r[0] < 0.75][:60]
    print(f"{'cov':>6}  {'lines':>12}  class")
    for pct, covered, n, name in shown:
        print(f"{pct:>6.1%}  {covered:>5}/{n:<6}  {name}")
    if not show_all and len(shown) < len(rows):
        print(f"\n({len(rows) - len(shown)} classes at >=75% not shown; pass --all)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
