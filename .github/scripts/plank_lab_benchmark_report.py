#!/usr/bin/env python3
"""Render a PR comparison from Plank-Lab BenchmarkDotNet logs."""

from __future__ import annotations

import argparse
import json
import math
import re
import statistics
from dataclasses import dataclass
from pathlib import Path


MARKER = "<!-- plank-pr-benchmark-comparison -->"
PLANK_CLASS_SUFFIX = "PlankBenchmarks"
ENCODING_ORDER = [
    "plain",
    "rle",
    "dictionary",
    "delta_binary_packed",
    "delta_length_byte_array",
    "delta_byte_array",
    "byte_stream_split",
]
ENCODING_LABELS = {
    "plain": "Plain",
    "rle": "RLE",
    "dictionary": "Dictionary",
    "delta_binary_packed": "Delta binary packed",
    "delta_length_byte_array": "Delta length byte array",
    "delta_byte_array": "Delta byte array",
    "byte_stream_split": "Byte stream split",
}
SUITE_LABELS = {"synthetic": "Synthetic", "real-world": "Real-world"}
LOG_NAME = re.compile(r"^(Synthetic|Real)-(Read|Write)-(base|head)-([12])\.log$")
BENCHMARK = re.compile(r"^// Benchmark: ([A-Za-z0-9_]+)\.(Read|Write):")
WORKLOAD_RESULT = re.compile(
    r"^WorkloadResult\s+\d+:\s+\d+ op,\s+([\d.]+)\s+(ns|us|μs|ms|s),")
UNIT_TO_MILLISECONDS = {"ns": 1e-6, "us": 1e-3, "μs": 1e-3, "ms": 1.0, "s": 1e3}


@dataclass(frozen=True)
class Comparison:
    suite: str
    operation: str
    case_id: str
    label: str
    data_type: str
    encoding: str
    row_count: int
    column_count: int
    base_ms: float
    head_ms: float
    base_p25_ms: float
    base_p75_ms: float
    head_p25_ms: float
    head_p75_ms: float
    delta_percent: float

    @property
    def status(self) -> str:
        if self.delta_percent < -2.0 and self.head_p75_ms < self.base_p25_ms:
            return "faster"
        if self.delta_percent > 2.0 and self.head_p25_ms > self.base_p75_ms:
            return "slower"
        return "noise"


def percentile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    position = fraction * (len(ordered) - 1)
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def load_comparisons(results_directory: Path, matrix_path: Path) -> tuple[list[Comparison], list[str]]:
    matrix = json.loads(matrix_path.read_text(encoding="utf-8"))
    by_stem = {item["stem"]: item for item in matrix}
    samples: dict[tuple[str, str, str, str], list[float]] = {}
    configurations: set[tuple[str, str]] = set()
    processors: set[str] = set()
    seen_logs: set[tuple[str, str, str, str]] = set()

    paths = sorted(results_directory.rglob("*.log"))
    if not paths:
        raise ValueError(f"No Plank-Lab logs found below {results_directory}.")

    for path in paths:
        name = LOG_NAME.match(path.name)
        if name is None:
            raise ValueError(f"Unexpected benchmark log name '{path.name}'.")
        suite_filter, expected_operation, variant, pass_number = name.groups()
        suite = "synthetic" if suite_filter == "Synthetic" else "real-world"
        operation = expected_operation.lower()
        configurations.add((suite, operation))
        log_key = (suite, operation, variant, pass_number)
        if log_key in seen_logs:
            raise ValueError(f"Duplicate benchmark pass {log_key}.")
        seen_logs.add(log_key)

        text = path.read_text(encoding="utf-8", errors="replace")
        processor = re.search(r"^(.+), \d+ CPU, \d+ logical", text, re.MULTILINE)
        if processor:
            processor_name = re.sub(
                r"\s+\d+(?:\.\d+)?GHz$", "", processor.group(1).strip(), flags=re.IGNORECASE)
            processors.add(processor_name)

        current: tuple[str, str, str, str] | None = None
        for line in text.splitlines():
            benchmark = BENCHMARK.match(line)
            if benchmark:
                class_name, measured_operation = benchmark.groups()
                if not class_name.endswith(PLANK_CLASS_SUFFIX):
                    raise ValueError(
                        f"{path.name} executed non-Plank benchmark '{class_name}'.")
                stem = class_name.removesuffix(PLANK_CLASS_SUFFIX)
                if stem not in by_stem:
                    raise ValueError(f"{path.name} contains unknown Plank-Lab case '{stem}'.")
                item = by_stem[stem]
                if item["suite"] != suite or measured_operation != expected_operation:
                    raise ValueError(f"{path.name} contains a benchmark outside its matrix cell.")
                current = (suite, operation, item["id"], variant)
                continue

            result = WORKLOAD_RESULT.match(line)
            if result and current is not None:
                value, unit = result.groups()
                samples.setdefault(current, []).append(
                    float(value) * UNIT_TO_MILLISECONDS[unit])

    comparisons: list[Comparison] = []
    for suite, operation in sorted(configurations):
        for item in (entry for entry in matrix if entry["suite"] == suite):
            base_key = (suite, operation, item["id"], "base")
            head_key = (suite, operation, item["id"], "head")
            if base_key not in samples or head_key not in samples:
                raise ValueError(
                    f"Missing base or head Plank-Lab samples for {suite}/{operation}/{item['id']}.")
            base = samples[base_key]
            head = samples[head_key]
            if len(base) < 2 or len(head) < 2:
                raise ValueError(f"Too few samples for {suite}/{operation}/{item['id']}.")
            base_ms = statistics.median(base)
            head_ms = statistics.median(head)
            data_type = item["dataTypes"][0] if len(item["dataTypes"]) == 1 else "Complete"
            comparisons.append(Comparison(
                suite=suite,
                operation=operation,
                case_id=item["id"],
                label=item["label"],
                data_type=data_type,
                encoding=item["encoding"],
                row_count=int(item["rowCount"]),
                column_count=int(item["columnCount"]),
                base_ms=base_ms,
                head_ms=head_ms,
                base_p25_ms=percentile(base, 0.25),
                base_p75_ms=percentile(base, 0.75),
                head_p25_ms=percentile(head, 0.25),
                head_p75_ms=percentile(head, 0.75),
                delta_percent=(head_ms / base_ms - 1.0) * 100.0,
            ))
    return comparisons, sorted(processors)


def result_badge(item: Comparison) -> str:
    magnitude = f"{abs(item.delta_percent):.1f}%"
    if item.status == "faster":
        return f"🟢 −{magnitude}"
    if item.status == "slower":
        return f"🔴 +{magnitude}"
    sign = "+" if item.delta_percent >= 0 else "−"
    return f"⚪ {sign}{magnitude}"


def render_matrix(comparisons: list[Comparison], suite: str, operation: str) -> str:
    selected = [item for item in comparisons
                if item.suite == suite and item.operation == operation]
    rows: list[str] = []
    for item in selected:
        if item.data_type not in rows:
            rows.append(item.data_type)
    encodings = [encoding for encoding in ENCODING_ORDER
                 if any(item.encoding == encoding for item in selected)]
    by_cell = {(item.data_type, item.encoding): item for item in selected}
    lines = [
        f"#### {SUITE_LABELS[suite]} · {operation.title()}",
        "",
        "| Data type | " + " | ".join(ENCODING_LABELS[value] for value in encodings) + " |",
        "|---|" + "---:|" * len(encodings),
    ]
    for data_type in rows:
        cells = []
        for encoding in encodings:
            item = by_cell.get((data_type, encoding))
            cells.append(result_badge(item) if item else "—")
        lines.append(f"| {data_type} | " + " | ".join(cells) + " |")
    return "\n".join(lines)


def chart_label(item: Comparison) -> str:
    data_types = {
        "bool": "Bool", "int32": "I32", "int64": "I64", "timestamp": "Time",
        "double": "F64", "string": "Str", "Complete": "All",
    }
    encodings = {
        "plain": "Plain", "rle": "RLE", "dictionary": "Dict",
        "delta_binary_packed": "Delta", "delta_length_byte_array": "DLen",
        "delta_byte_array": "DByte", "byte_stream_split": "BSS",
    }
    return f"{data_types.get(item.data_type, item.data_type)}-{encodings[item.encoding]}"


def render_chart(comparisons: list[Comparison], suite: str, operation: str) -> str:
    selected = [item for item in comparisons
                if item.suite == suite and item.operation == operation]
    ratios = [round(item.head_ms / item.base_ms * 100.0, 1) for item in selected]
    movement = max(abs(value - 100.0) for value in ratios)
    span = max(5, int(math.ceil((movement + 1) / 5.0) * 5))
    labels = ", ".join(f'"{chart_label(item)}"' for item in selected)
    values = ", ".join(f"{value:g}" for value in ratios)
    baseline = ", ".join("100" for _ in selected)
    return "\n".join([
        "```mermaid",
        "xychart-beta horizontal",
        '    title "Runtime index — base = 100, lower is faster"',
        f"    x-axis [{labels}]",
        f'    y-axis "Base = 100" {100 - span} --> {100 + span}',
        f"    bar [{values}]",
        f"    line [{baseline}]",
        "```",
    ])


def build_report(comparisons: list[Comparison], processors: list[str], base_sha: str,
                 head_sha: str, plank_lab_sha: str, run_url: str) -> str:
    faster = sum(item.status == "faster" for item in comparisons)
    slower = sum(item.status == "slower" for item in comparisons)
    noise = len(comparisons) - faster - slower
    configurations = []
    for item in comparisons:
        key = (item.suite, item.operation)
        if key not in configurations:
            configurations.append(key)

    sections = [
        MARKER,
        "## ⚡ Plank-Lab benchmark comparison",
        "",
        f"Comparing Plank base `{base_sha[:8]}` with PR `{head_sha[:8]}`.",
        f"This runs the [Plank-Lab published matrix]"
        f"(https://github.com/Kuinox/Plank-Lab/tree/{plank_lab_sha}) and filters execution to "
        "`*PlankBenchmarks`—ParquetSharp and Parquet.NET are not measured.",
        "Each matrix slice ran base / PR / PR / base on one runner under BenchmarkDotNet.",
        "",
        f"**{faster} faster · {noise} within noise · {slower} slower**",
        "",
        "> Chart guide: the vertical 100 line is base latency. A bar endpoint left of 100 is "
        "faster; an endpoint right of 100 is slower.",
        "",
        "> A colored result requires a change beyond ±2% and non-overlapping interquartile ranges. "
        "Every percentage remains visible; hosted-runner results are advisory.",
        "",
    ]
    for suite, operation in configurations:
        sections.extend([
            render_matrix(comparisons, suite, operation),
            "",
            render_chart(comparisons, suite, operation),
            "",
        ])

    details = [
        "<details>",
        "<summary>Exact median latency</summary>",
        "",
        "| Suite | Operation | Case | Encoding | Shape | Base | PR | Change |",
        "|---|---|---|---|---|---:|---:|---:|",
    ]
    for item in comparisons:
        shape = f"{item.row_count:,} rows × {item.column_count} columns"
        details.append(
            f"| {SUITE_LABELS[item.suite]} | {item.operation.title()} | {item.label} | "
            f"{ENCODING_LABELS[item.encoding]} | {shape} | "
            f"{item.base_ms:.3f} ms | {item.head_ms:.3f} ms | {result_badge(item)} |")
    details.extend(["", "</details>", ""])
    sections.extend(details)

    processor_text = " · ".join(processors) if processors else "GitHub-hosted Linux runners"
    sections.extend([
        f"Runners: {processor_text}.",
        f"[Workflow run, BenchmarkDotNet logs, and report artifact]({run_url})",
        "",
        "<sub>🟢 faster · ⚪ distributions overlap / movement ≤2% · 🔴 slower.</sub>",
    ])
    return "\n".join(sections)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--matrix", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--summary", type=Path)
    parser.add_argument("--base-sha", required=True)
    parser.add_argument("--head-sha", required=True)
    parser.add_argument("--plank-lab-sha", required=True)
    parser.add_argument("--run-url", required=True)
    args = parser.parse_args()

    comparisons, processors = load_comparisons(args.results, args.matrix)
    report = build_report(
        comparisons, processors, args.base_sha, args.head_sha, args.plank_lab_sha, args.run_url)
    args.output.write_text(report + "\n", encoding="utf-8")
    if args.summary:
        args.summary.write_text(report.replace(MARKER + "\n", "") + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
