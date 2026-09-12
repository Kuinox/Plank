#!/usr/bin/env python3
"""Render a PR comparison from Plank-Lab BenchmarkDotNet logs."""

from __future__ import annotations

import argparse
import json
import math
import re
import statistics
from dataclasses import asdict, dataclass
from pathlib import Path


MARKER = "<!-- plank-pr-benchmark-comparison -->"
PLANK_CLASS_SUFFIX = "PlankBenchmarks"
NOISE_STANDARD_DEVIATIONS = 3.0
# Diagnostic tolerance, not a statistical significance threshold.
STABILITY_PERCENT = 5.0
MIN_SAMPLES = 15
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
WORKLOAD_ACTUAL = re.compile(
    r"^WorkloadActual\s+(\d+):\s+(\d+) op,\s+([\d.]+)\s+(ns|us|μs|ms|s),")
UNIT_TO_MILLISECONDS = {"ns": 1e-6, "us": 1e-3, "μs": 1e-3, "ms": 1.0, "s": 1e3}


@dataclass(frozen=True)
class PassSummary:
    samples_ms: tuple[float, ...]
    configuration: str
    prewarm: str | None

    @property
    def median_ms(self) -> float:
        return statistics.median(self.samples_ms)

    @property
    def blocks_ms(self) -> tuple[float, ...]:
        count = len(self.samples_ms)
        return tuple(statistics.median(self.samples_ms[i * count // 3:(i + 1) * count // 3])
                     for i in range(3)) if count >= MIN_SAMPLES else ()

    @property
    def drift_percent(self) -> float:
        blocks = self.blocks_ms
        return (max(blocks) / min(blocks) - 1) * 100 if blocks else math.inf

    @property
    def interval_ms(self) -> tuple[float, float]:
        radius = NOISE_STANDARD_DEVIATIONS * statistics.stdev(self.samples_ms)
        return self.median_ms - radius, self.median_ms + radius


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
    base_passes: tuple[PassSummary, ...]
    head_passes: tuple[PassSummary, ...]
    workload: str = "row"

    @property
    def base_ms(self) -> float:
        return statistics.mean(p.median_ms for p in self.base_passes)

    @property
    def head_ms(self) -> float:
        return statistics.mean(p.median_ms for p in self.head_passes)

    @property
    def delta_percent(self) -> float:
        return (self.head_ms / self.base_ms - 1) * 100

    @property
    def repeat_percent(self) -> float:
        return max((max(p.median_ms for p in passes) / min(p.median_ms for p in passes) - 1) * 100
                   for passes in (self.base_passes, self.head_passes))

    @property
    def drift_percent(self) -> float:
        return max(p.drift_percent for p in self.base_passes + self.head_passes)

    @property
    def status(self) -> str:
        if any(len(p.samples_ms) < MIN_SAMPLES for p in self.base_passes + self.head_passes):
            return "insufficient"
        if max(self.repeat_percent, self.drift_percent) > STABILITY_PERCENT:
            return "unstable"
        # Every process interval must separate in the same direction. These are
        # descriptive median +/- 3 SD envelopes, not confidence intervals.
        base = [p.interval_ms for p in self.base_passes]
        head = [p.interval_ms for p in self.head_passes]
        if max(high for _, high in head) < min(low for low, _ in base):
            return "faster"
        if min(low for low, _ in head) > max(high for _, high in base):
            return "slower"
        return "inconclusive"


def load_comparisons(results_directory: Path, matrix_path: Path,
                     require_prewarm: bool = False) -> tuple[list[Comparison], list[str]]:
    matrix = json.loads(matrix_path.read_text(encoding="utf-8"))
    # Lab generates column adapters from the same matrix with a Column suffix.
    matrix = [dict(item, workload="row") for item in matrix] + [
        dict(item, stem=item["stem"] + "Column", id=item["id"] + "-column", workload="column")
        for item in matrix
    ]
    by_stem = {item["stem"]: item for item in matrix}
    samples: dict[tuple[str, str, str, str, str], list[float]] = {}
    configurations_by_pass = {}
    expected_counts = {}
    prewarm = {}
    configurations: set[tuple[str, str, str]] = set()
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

        current: tuple[str, str, str, str, str] | None = None
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
                configurations.add((suite, operation, item["workload"]))
                current = (suite, operation, item["id"], variant, pass_number)
                if current in samples:
                    raise ValueError(f"Duplicate benchmark {current}.")
                count = re.search(r"IterationCount=(\d+)", line)
                if count is None:
                    raise ValueError(f"Missing IterationCount for {current}.")
                samples[current] = []
                expected_counts[current] = int(count.group(1))
                configurations_by_pass[current] = line
                continue

            if current is not None and line.startswith("// PR prewarm:"):
                prewarm[current] = line
            result = WORKLOAD_ACTUAL.match(line)
            if result and current is not None:
                index, operations, value, unit = result.groups()
                if int(index) != len(samples[current]) + 1 or int(operations) <= 0:
                    raise ValueError(f"Invalid measurement order/operations for {current}.")
                value_ms = float(value) * UNIT_TO_MILLISECONDS[unit] / int(operations)
                if not math.isfinite(value_ms) or value_ms <= 0:
                    raise ValueError(f"Invalid measurement for {current}.")
                samples[current].append(value_ms)

        if current is None:
            raise ValueError(f"{path.name} contains no Plank-Lab benchmarks.")

    expected_logs = {(suite, operation, variant, str(p))
                     for suite in SUITE_LABELS for operation in ("read", "write")
                     for variant in ("base", "head") for p in (1, 2)}
    if seen_logs != expected_logs:
        raise ValueError(f"Incomplete benchmark matrix; missing logs: {sorted(expected_logs - seen_logs)}.")

    for key, values in samples.items():
        if require_prewarm:
            marker = re.fullmatch(r"// PR prewarm: (\d+) iterations, ([\d.]+) seconds", prewarm.get(key, ""))
            if marker is None or int(marker[1]) < 32 or float(marker[2]) < 10:
                raise ValueError(f"Missing or insufficient timed prewarm for {key}.")
        if len(values) != expected_counts[key] or len(values) < 2:
            raise ValueError(f"Incomplete measurements for {key}: expected {expected_counts[key]}, got {len(values)}.")

    comparisons: list[Comparison] = []
    for suite, operation, workload in sorted(configurations):
        for item in (entry for entry in matrix if entry["suite"] == suite and entry["workload"] == workload):
            series = []
            for variant in ("base", "head"):
                keys = [(suite, operation, item["id"], variant, str(p)) for p in (1, 2)]
                if any(key not in samples for key in keys):
                    raise ValueError(f"Missing base or head pass for {suite}/{operation}/{item['id']}.")
                series.append(tuple(PassSummary(tuple(samples[key]), configurations_by_pass[key],
                                                prewarm.get(key)) for key in keys))
            if len({len(p.samples_ms) for passes in series for p in passes}) != 1:
                raise ValueError(f"Mismatched iteration counts for {item['id']}.")
            data_type = item["dataTypes"][0] if len(item["dataTypes"]) == 1 else "Complete"
            comparisons.append(Comparison(
                suite=suite,
                workload=workload,
                operation=operation,
                case_id=item["id"],
                label=item["label"],
                data_type=data_type,
                encoding=item["encoding"],
                row_count=int(item["rowCount"]),
                column_count=int(item["columnCount"]),
                base_passes=series[0],
                head_passes=series[1],
            ))
    return comparisons, sorted(processors)


def result_badge(item: Comparison) -> str:
    magnitude = f"{abs(item.delta_percent):.1f}%"
    if item.status == "faster":
        return f"🟢 −{magnitude}"
    if item.status == "slower":
        return f"🔴 +{magnitude}"
    sign = "+" if item.delta_percent >= 0 else "−"
    symbol = "🟠" if item.status in ("unstable", "insufficient") else "⚪"
    return f"{symbol} {sign}{magnitude}"


def render_matrix(comparisons: list[Comparison], suite: str, operation: str, workload: str = "row") -> str:
    selected = [item for item in comparisons
                if item.suite == suite and item.operation == operation and item.workload == workload]
    rows: list[str] = []
    for item in selected:
        if item.data_type not in rows:
            rows.append(item.data_type)
    encodings = [encoding for encoding in ENCODING_ORDER
                 if any(item.encoding == encoding for item in selected)]
    by_cell = {(item.data_type, item.encoding): item for item in selected}
    lines = [
        f"#### {SUITE_LABELS[suite]} · {workload.title()} · {operation.title()}",
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


def render_chart(comparisons: list[Comparison], suite: str, operation: str, workload: str = "row") -> str:
    selected = [item for item in comparisons
                if item.suite == suite and item.operation == operation and item.workload == workload]
    ratios = [round(item.head_ms / item.base_ms * 100.0, 1) for item in selected]
    # A common 0..200 scale for ordinary reports; expand all charts together
    # only when an actual ratio exceeds it. Uncertainty never rescales bars.
    upper = max(200, math.ceil(max(item.head_ms / item.base_ms * 100
                                   for item in comparisons) / 50) * 50)
    labels = ", ".join(f'"{chart_label(item)}"' for item in selected)
    values = ", ".join(f"{value:g}" for value in ratios)
    baseline = ", ".join("100" for _ in selected)
    return "\n".join([
        "```mermaid",
        "xychart-beta horizontal",
        '    title "Runtime index (equal process weights)"',
        f"    x-axis [{labels}]",
        f'    y-axis "Base = 100" 0 --> {upper}',
        f"    bar [{values}]",
        f"    line [{baseline}]",
        "```",
    ])


def build_report(comparisons: list[Comparison], processors: list[str], base_sha: str,
                 head_sha: str, plank_lab_sha: str, run_url: str) -> str:
    faster = sum(item.status == "faster" for item in comparisons)
    slower = sum(item.status == "slower" for item in comparisons)
    unstable = sum(item.status in ("unstable", "insufficient") for item in comparisons)
    inconclusive = len(comparisons) - faster - slower - unstable
    configurations = []
    for item in comparisons:
        key = (item.suite, item.operation, item.workload)
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
        f"**{faster} faster · {inconclusive} inconclusive · {unstable} unstable/insufficient · {slower} slower**",
        "",
        "> Bars compare the means of the two independent process medians. All ordered "
        "WorkloadActual samples are retained, normalized per operation. No pass is pooled or dropped. "
        "Charts share a 0–200 scale, expanded together only for ratios above 200.",
        "",
        f"> 🟠 flags more than {STABILITY_PERCENT:g}% variation between same-code process medians "
        "or between three consecutive measurement-block medians within any process; fewer than "
        f"{MIN_SAMPLES} samples per process are insufficient. This tolerance is diagnostic, not a significance test.",
        "",
        "> Otherwise, color requires every base process median ±3 SD envelope to separate from "
        "every PR envelope in the same direction. These are descriptive envelopes, not confidence "
        "intervals. Inconclusive does not mean equivalent. Hosted-runner results remain advisory.",
        "",
    ]
    for suite, operation, workload in configurations:
        sections.extend([
            render_matrix(comparisons, suite, operation, workload),
            "",
            render_chart(comparisons, suite, operation, workload),
            "",
        ])

    details = [
        "<details>",
        "<summary>Process medians and stability diagnostics</summary>",
        "",
        "| Suite | Workload | Operation | Case | Encoding | Shape | Base passes (ms) | PR passes (ms) | Repeat / drift | Change |",
        "|---|---|---|---|---|---|---:|---:|---:|---:|",
    ]
    for item in comparisons:
        shape = f"{item.row_count:,} rows × {item.column_count} columns"
        details.append(
            f"| {SUITE_LABELS[item.suite]} | {item.workload.title()} | {item.operation.title()} | {item.label} | "
            f"{ENCODING_LABELS[item.encoding]} | {shape} | "
            f"{' / '.join(f'{p.median_ms:.3f}' for p in item.base_passes)} | "
            f"{' / '.join(f'{p.median_ms:.3f}' for p in item.head_passes)} | "
            f"{item.repeat_percent:.1f}% / {item.drift_percent:.1f}% | {result_badge(item)} |")
    details.extend(["", "</details>", ""])
    sections.extend(details)

    processor_text = " · ".join(processors) if processors else "GitHub-hosted Linux runners"
    sections.extend([
        f"Runners: {processor_text}.",
        f"[Workflow run, BenchmarkDotNet logs, and report artifact]({run_url})",
        "",
        "<sub>🟢 faster · ⚪ inconclusive · 🟠 unstable/insufficient · 🔴 slower.</sub>",
    ])
    return "\n".join(sections)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--matrix", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--summary", type=Path)
    parser.add_argument("--require-prewarm", action="store_true")
    parser.add_argument("--base-sha", required=True)
    parser.add_argument("--head-sha", required=True)
    parser.add_argument("--plank-lab-sha", required=True)
    parser.add_argument("--run-url", required=True)
    args = parser.parse_args()

    comparisons, processors = load_comparisons(args.results, args.matrix, args.require_prewarm)
    report = build_report(
        comparisons, processors, args.base_sha, args.head_sha, args.plank_lab_sha, args.run_url)
    analysis = [dict(asdict(item), status=item.status, delta_percent=item.delta_percent,
                     repeat_percent=item.repeat_percent,
                     drift_percent=item.drift_percent if math.isfinite(item.drift_percent) else None)
                for item in comparisons]
    args.output.with_suffix(".json").write_text(json.dumps(analysis, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    args.output.write_text(report + "\n", encoding="utf-8")
    if args.summary:
        args.summary.write_text(report.replace(MARKER + "\n", "") + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
