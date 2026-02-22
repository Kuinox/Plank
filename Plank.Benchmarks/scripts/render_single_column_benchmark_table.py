#!/usr/bin/env python3

from __future__ import annotations

import argparse
import csv
import re
import sys
from pathlib import Path

LIBRARY_ORDER = ["Plank", "ParquetSharp", "Parquet.Net"]
TYPE_ORDER = ["bool", "int32", "int64", "float", "double", "string"]
ENCODING_ORDER = [
    "plain",
    "dictionary",
    "delta_binary_packed",
    "delta_length_byte_array",
    "delta_byte_array",
    "byte_stream_split",
]
SCENARIO_PATTERN = re.compile(r"(bool|int32|int64|float|double|string)\|([a-z0-9_]+)")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Render a markdown comparison table from BenchmarkDotNet CSV output.",
    )
    parser.add_argument("csv_path", type=Path, help="Path to the BenchmarkDotNet CSV report.")
    parser.add_argument(
        "--metric",
        default="Mean",
        help="CSV column to render for each library (default: Mean).",
    )
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
    table = build_table(rows, args.metric)
    render_markdown(table)
    return 0


def load_rows(csv_path: Path) -> list[dict[str, str]]:
    if not csv_path.exists():
        raise SystemExit(f"CSV file was not found: {csv_path}")

    with csv_path.open("r", encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        return list(reader)


def filter_rows(rows: list[dict[str, str]], rows_filter: str | None) -> list[dict[str, str]]:
    detected_rows = sorted(
        {
            normalize_rows_value(row.get("Rows", ""))
            for row in rows
            if row.get("Rows", "").strip()
        }
    )

    if rows_filter is not None:
        target = normalize_rows_value(rows_filter)
        filtered = [row for row in rows if normalize_rows_value(row.get("Rows", "")) == target]
        if not filtered:
            raise SystemExit(
                f"No rows matched --rows={rows_filter}. Available Rows values: {', '.join(detected_rows) or 'none'}"
            )
        return filtered

    if len(detected_rows) > 1:
        joined = ", ".join(detected_rows)
        raise SystemExit(f"CSV has multiple Rows values ({joined}). Re-run with --rows to select one.")

    return rows


def build_table(rows: list[dict[str, str]], metric_column: str) -> dict[tuple[str, str], dict[str, str]]:
    table: dict[tuple[str, str], dict[str, str]] = {}
    for row in rows:
        library = parse_library(row.get("Method", ""))
        if library is None:
            continue

        scenario = parse_scenario(row)
        if scenario is None:
            continue

        metric_value = row.get(metric_column)
        if metric_value is None:
            raise SystemExit(f"CSV column '{metric_column}' was not found.")

        typed_encoding = table.setdefault(scenario, {})
        typed_encoding[library] = metric_value.strip() or "n/a"

    if not table:
        raise SystemExit("No benchmark rows matched expected methods/scenarios.")
    return table


def parse_library(method_name: str) -> str | None:
    method_name = method_name.strip()
    if method_name.startswith("WritePlank"):
        return "Plank"
    if method_name.startswith("WriteParquetSharp"):
        return "ParquetSharp"
    if method_name.startswith("WriteParquetNet"):
        return "Parquet.Net"
    return None


def parse_scenario(row: dict[str, str]) -> tuple[str, str] | None:
    data_type = row.get("DataType", "").strip()
    encoding = row.get("EncodingName", "").strip()
    if data_type and encoding:
        return data_type, encoding

    for key in ("scenario", "Scenario", "Arguments"):
        value = row.get(key, "").strip().strip('"')
        if not value:
            continue

        if "|" in value:
            left, right = value.split("|", 1)
            left = left.strip()
            right = right.strip()
            if left and right:
                return left, right

        match = SCENARIO_PATTERN.search(value)
        if match is not None:
            return match.group(1), match.group(2)

    return None


def render_markdown(table: dict[tuple[str, str], dict[str, str]]) -> None:
    print("| Type | Encoding | Plank | ParquetSharp | Parquet.Net |")
    print("| --- | --- | --- | --- | --- |")
    for data_type, encoding in sorted(table.keys(), key=sort_key):
        values = table[(data_type, encoding)]
        plank = values.get("Plank", "n/a")
        parquet_sharp = values.get("ParquetSharp", "n/a")
        parquet_net = values.get("Parquet.Net", "n/a")
        print(f"| {data_type} | {encoding} | {plank} | {parquet_sharp} | {parquet_net} |")


def sort_key(item: tuple[str, str]) -> tuple[int, int, str, str]:
    data_type, encoding = item
    try:
        type_index = TYPE_ORDER.index(data_type)
    except ValueError:
        type_index = len(TYPE_ORDER)

    try:
        encoding_index = ENCODING_ORDER.index(encoding)
    except ValueError:
        encoding_index = len(ENCODING_ORDER)

    return type_index, encoding_index, data_type, encoding


def normalize_rows_value(value: str) -> str:
    return value.replace(",", "").replace("_", "").strip()


if __name__ == "__main__":
    sys.exit(main())
