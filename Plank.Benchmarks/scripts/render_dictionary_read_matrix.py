#!/usr/bin/env python3

from __future__ import annotations

import argparse
import csv
import re
import sys
from pathlib import Path

METHOD_PLAIN = "ReadPlainWithParquetSharp"
METHOD_DICTIONARY = "ReadDictionaryWithParquetSharp"
MEAN_PATTERN = re.compile(r"^\s*([0-9]+(?:\.[0-9]+)?)\s*(ns|us|ms|s)\s*$", re.IGNORECASE)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Render a markdown matrix for ParquetSharp read speed on plain vs dictionary files.",
    )
    parser.add_argument("csv_path", type=Path, help="Path to BenchmarkDotNet CSV output.")
    parser.add_argument(
        "--rows",
        default=None,
        help="Optional row-count filter when CSV includes multiple Rows values.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    rows = load_rows(args.csv_path)
    rows = filter_rows(rows, args.rows)
    matrix = build_matrix(rows)
    render_markdown(matrix)
    return 0


def load_rows(csv_path: Path) -> list[dict[str, str]]:
    if not csv_path.exists():
        raise SystemExit(f"CSV file not found: {csv_path}")

    with csv_path.open("r", encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        return list(reader)


def filter_rows(rows: list[dict[str, str]], rows_filter: str | None) -> list[dict[str, str]]:
    if rows_filter is None:
        return rows

    target = normalize_rows(rows_filter)
    filtered = [row for row in rows if normalize_rows(row.get("Rows", "")) == target]
    if not filtered:
        raise SystemExit(f"No rows matched --rows={rows_filter}")
    return filtered


def build_matrix(rows: list[dict[str, str]]) -> dict[int, dict[str, tuple[str, float]]]:
    matrix: dict[int, dict[str, tuple[str, float]]] = {}
    for row in rows:
        method = row.get("Method", "").strip()
        if method not in (METHOD_PLAIN, METHOD_DICTIONARY):
            continue

        unique_percent_raw = row.get("UniquePercent", "").strip()
        if not unique_percent_raw:
            continue
        unique_percent = int(unique_percent_raw)

        mean_text = row.get("Mean", "").strip()
        mean_ns = parse_mean_to_ns(mean_text)
        if mean_ns is None:
            continue

        slot = matrix.setdefault(unique_percent, {})
        slot[method] = (mean_text, mean_ns)

    if not matrix:
        raise SystemExit("No matching benchmark rows found.")
    return matrix


def render_markdown(matrix: dict[int, dict[str, tuple[str, float]]]) -> None:
    print("| Unique % | Plain mean | Dictionary mean | Faster | Dict/Plain |")
    print("| --- | --- | --- | --- | --- |")
    for unique_percent in sorted(matrix.keys()):
        slot = matrix[unique_percent]
        plain = slot.get(METHOD_PLAIN)
        dictionary = slot.get(METHOD_DICTIONARY)
        if plain is None or dictionary is None:
            continue

        plain_text, plain_ns = plain
        dictionary_text, dictionary_ns = dictionary
        faster = "plain" if plain_ns <= dictionary_ns else "dictionary"
        ratio = dictionary_ns / plain_ns if plain_ns > 0 else float("nan")
        print(
            f"| {unique_percent} | {plain_text} | {dictionary_text} | {faster} | {ratio:.2f}x |"
        )


def parse_mean_to_ns(mean_text: str) -> float | None:
    match = MEAN_PATTERN.match(mean_text)
    if match is None:
        return None

    value = float(match.group(1))
    unit = match.group(2).lower()
    if unit == "ns":
        return value
    if unit == "us":
        return value * 1_000.0
    if unit == "ms":
        return value * 1_000_000.0
    if unit == "s":
        return value * 1_000_000_000.0
    return None


def normalize_rows(raw: str) -> str:
    return raw.replace(",", "").replace("_", "").strip()


if __name__ == "__main__":
    sys.exit(main())
