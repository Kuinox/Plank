#!/usr/bin/env python3
"""Render a PR comparison from Plank-Lab BenchmarkDotNet logs."""

from __future__ import annotations

import argparse
import json
import math
import re
import statistics
from dataclasses import asdict, dataclass, replace
from pathlib import Path


MARKER = "<!-- plank-pr-benchmark-comparison -->"
PLANK_CLASS_SUFFIX = "PlankBenchmarks"
LATE_BLOCKS = 5
MIN_BLOCK_SAMPLES = 5
NOISE_STANDARD_DEVIATIONS = 3.0
MAD_TO_STANDARD_DEVIATION = 1.4826
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
    # Chronological, unfiltered per-operation measurements, including startup.
    samples_ms: tuple[float, ...]
    late_start: int
    late_blocks_ms: tuple[float, ...]

    @classmethod
    def create(cls, samples: list[float]) -> PassSummary:
        start = len(samples) // 2
        tail = samples[start:]
        blocks = ()
        if len(tail) >= LATE_BLOCKS * MIN_BLOCK_SAMPLES:
            blocks = tuple(statistics.median(tail[i * len(tail) // LATE_BLOCKS:
                                                    (i + 1) * len(tail) // LATE_BLOCKS])
                           for i in range(LATE_BLOCKS))
        return cls(tuple(samples), start, blocks)

    @property
    def late_ms(self) -> float:
        return statistics.median(self.samples_ms[self.late_start:])


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
    noise_window_percent: float = 0.0

    @property
    def base_ms(self) -> float:
        return statistics.median(p.late_ms for p in self.base_passes)

    @property
    def head_ms(self) -> float:
        return statistics.median(p.late_ms for p in self.head_passes)

    @property
    def delta_percent(self) -> float:
        return (self.head_ms / self.base_ms - 1) * 100

    @property
    def observed_range(self) -> tuple[float, float] | None:
        passes = self.base_passes + self.head_passes
        if any(not p.late_blocks_ms for p in passes):
            return None
        base = [v for p in self.base_passes for v in p.late_blocks_ms]
        head = [v for p in self.head_passes for v in p.late_blocks_ms]
        # All cross-pass and cross-block comparisons, not just convenient pairs.
        # This is a descriptive envelope, NOT a confidence interval.
        return ((min(head) / max(base) - 1) * 100,
                (max(head) / min(base) - 1) * 100)

    @property
    def status(self) -> str:
        bounds = self.observed_range
        if bounds is None:
            return "insufficient"
        if bounds[1] < -self.noise_window_percent:
            return "faster"
        if bounds[0] > self.noise_window_percent:
            return "slower"
        return "inconclusive"


def estimate_noise_window_percent(comparisons: list[Comparison]) -> float:
    """Estimate same-revision runner noise from late pass-to-pass drift.

    A benchmark pass is the independent unit here; individual iterations in a
    pass are correlated by the process, JIT, and runner state. The robust MAD
    estimate keeps one pathological case from defining the noise window.
    """
    drifts = []
    for item in comparisons:
        for passes in (item.base_passes, item.head_passes):
            if len(passes) != 2:
                continue
            first, second = (pass_summary.late_ms for pass_summary in passes)
            drifts.append((second / first - 1.0) * 100.0)
    if len(drifts) < 2:
        return 0.0

    center = statistics.median(drifts)
    mad = statistics.median(abs(value - center) for value in drifts)
    robust_standard_deviation = MAD_TO_STANDARD_DEVIATION * mad
    return abs(center) + NOISE_STANDARD_DEVIATIONS * robust_standard_deviation


def load_comparisons(results_directory: Path, matrix_path: Path) -> tuple[list[Comparison], list[str]]:
    matrix = json.loads(matrix_path.read_text(encoding="utf-8"))
    # Lab generates column adapters from the same matrix with a Column suffix.
    matrix = [dict(item, workload="row") for item in matrix] + [
        dict(item, stem=item["stem"] + "Column", id=item["id"] + "-column", workload="column")
        for item in matrix
    ]
    by_stem = {item["stem"]: item for item in matrix}
    samples: dict[tuple[str, str, str, str, str], list[float]] = {}
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
        expected_counts = {}
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
                samples[current] = []
                count = re.search(r"IterationCount=(\d+)", line)
                if count is None:
                    raise ValueError(f"Missing IterationCount for {current}.")
                expected_counts[current] = int(count.group(1))
                continue

            result = WORKLOAD_ACTUAL.match(line)
            if result and current is not None:
                index, operations, value, unit = result.groups()
                if int(index) != len(samples[current]) + 1:
                    raise ValueError(f"Non-contiguous measurement order for {current}.")
                if int(operations) <= 0:
                    raise ValueError(f"Invalid operation count for {current}.")
                milliseconds = float(value) * UNIT_TO_MILLISECONDS[unit] / int(operations)
                if not math.isfinite(milliseconds) or milliseconds <= 0:
                    raise ValueError(f"Invalid measurement for {current}.")
                samples[current].append(milliseconds)

        for key, count in expected_counts.items():
            if len(samples[key]) != count or count < 2:
                raise ValueError(f"Incomplete measurements for {key}: expected {count}, got {len(samples[key])}.")
        if current is None:
            raise ValueError(f"{path.name} contains no Plank-Lab benchmarks.")

    comparisons: list[Comparison] = []
    for suite, operation, workload in sorted(configurations):
        for item in (entry for entry in matrix if entry["suite"] == suite and entry["workload"] == workload):
            series = []
            for variant in ("base", "head"):
                keys = [(suite, operation, item["id"], variant, str(p)) for p in (1, 2)]
                if any(key not in samples for key in keys):
                    raise ValueError(f"Missing base or head pass for {suite}/{operation}/{item['id']}.")
                series.append(tuple(PassSummary.create(samples[key]) for key in keys))
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
    noise_window_percent = estimate_noise_window_percent(comparisons)
    comparisons = [replace(item, noise_window_percent=noise_window_percent)
                   for item in comparisons]
    return comparisons, sorted(processors)


def format_range(item: Comparison) -> str:
    bounds = item.observed_range
    return f"{bounds[0]:+.1f}% to {bounds[1]:+.1f}%" if bounds else "insufficient samples"


def write_analysis(path: Path, comparisons: list[Comparison]) -> None:
    path.write_text(json.dumps([
        dict(asdict(item), status=item.status, delta_percent=item.delta_percent,
             observed_range_percent=item.observed_range)
        for item in comparisons], indent=2) + "\n", encoding="utf-8")


def result_badge(item: Comparison) -> str:
    magnitude = f"{abs(item.delta_percent):.1f}%"
    if item.status == "faster":
        return f"🟢 −{magnitude}"
    if item.status == "slower":
        return f"🔴 +{magnitude}"
    sign = "+" if item.delta_percent >= 0 else "−"
    return f"⚪ {sign}{magnitude}"


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
    noise_window = selected[0].noise_window_percent if selected else 0.0
    span = max(5, int(math.ceil((max(
        max(abs(v - 100) for v in ratios), noise_window) + 1) / 5) * 5))
    labels = ", ".join(f'"{chart_label(item)}"' for item in selected)
    values = ", ".join(f"{value:g}" for value in ratios)
    lower_noise = ", ".join(f"{max(0.0, 100.0 - noise_window):g}" for _ in selected)
    baseline = ", ".join("100" for _ in selected)
    upper_noise = ", ".join(f"{100.0 + noise_window:g}" for _ in selected)
    return "\n".join([
        "```mermaid",
        "xychart-beta horizontal",
        '    title "Late-half runtime index with measured pass noise"',
        f"    x-axis [{labels}]",
        f'    y-axis "Base = 100" {100 - span} --> {100 + span}',
        f"    bar [{values}]",
        f"    line [{lower_noise}]",
        f"    line [{baseline}]",
        f"    line [{upper_noise}]",
        "```",
    ])


def build_report(comparisons: list[Comparison], processors: list[str], base_sha: str,
                 head_sha: str, plank_lab_sha: str, run_url: str) -> str:
    faster = sum(item.status == "faster" for item in comparisons)
    slower = sum(item.status == "slower" for item in comparisons)
    inconclusive = len(comparisons) - faster - slower
    noise_window_percent = comparisons[0].noise_window_percent if comparisons else 0.0
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
        f"**{faster} consistently faster in late blocks · {inconclusive} inconclusive · {slower} consistently slower in late blocks**",
        "",
        "> Bars show the ratio of equally weighted per-pass late-half medians (base = 100). "
        "Each pass keeps its first half as startup/tiering diagnostics; the fixed second half "
        "is split into five consecutive, nearly equal blocks (at least five samples each).",
        "",
        f"> The boundary lines show ±{noise_window_percent:.1f}% measured runner noise. "
        "Noise is estimated from same-revision pass-to-pass late-median drift across the matrix "
        "using three robust standard deviations (1.4826 × MAD).",
        "",
        "> Color requires every PR late-block median to be below every base block median, "
        "or every PR block above every base block, across both passes, and the observed range "
        "must clear the measured noise window. Otherwise the result is inconclusive, never "
        "evidence of equivalence. The observed range is not a confidence interval.",
        "",
        "> These are descriptive late-run comparisons, not a significance test or proof of "
        "steady-state throughput. Tiering can continue into the late half. Inspect the ordered "
        "blocks and first-to-last block drift in the diagnostics artifact; only two process passes "
        "cannot establish run-to-run uncertainty. Hosted-runner results remain advisory.",
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
        "<summary>Late-half latency and observed block range</summary>",
        "",
        "| Suite | Workload | Operation | Case | Encoding | Shape | Base passes (ms) | PR passes (ms) | Noise | Observed change range | Change |",
        "|---|---|---|---|---|---|---:|---:|---:|---:|---:|",
    ]
    for item in comparisons:
        shape = f"{item.row_count:,} rows × {item.column_count} columns"
        details.append(
            f"| {SUITE_LABELS[item.suite]} | {item.workload.title()} | {item.operation.title()} | {item.label} | "
            f"{ENCODING_LABELS[item.encoding]} | {shape} | "
            f"{item.base_passes[0].late_ms:.3f} / {item.base_passes[1].late_ms:.3f} | "
            f"{item.head_passes[0].late_ms:.3f} / {item.head_passes[1].late_ms:.3f} | "
            f"±{item.noise_window_percent:.1f}% | "
            f"{format_range(item)} | {result_badge(item)} |")
    details.extend(["", "</details>", ""])
    sections.extend(details)

    processor_text = " · ".join(processors) if processors else "GitHub-hosted Linux runners"
    sections.extend([
        f"Runners: {processor_text}.",
        f"[Workflow run, BenchmarkDotNet logs, and report artifact]({run_url})",
        "",
        "<sub>🟢 faster · ⚪ inconclusive or insufficient; not equivalent · 🔴 slower.</sub>",
    ])
    return "\n".join(sections)


def build_diagnostics(comparisons: list[Comparison]) -> str:
    sections = []
    sections.extend(["<details>", "<summary>Startup, pass variation, and ordered late blocks (ms)</summary>", "",
                     "Each cell lists pass 1 / pass 2. Drift is last late-block median versus first; "
                     "a small drift does not prove stationarity. Raw samples and block boundaries "
                     "are retained in the analysis JSON artifact.", "",
                     "| Case | Variant | First sample | First-half median | Late-half median | Late blocks (in order) | Drift |",
                     "|---|---|---:|---:|---:|---|---:|"])
    for item in comparisons:
        for variant, passes in (("Base", item.base_passes), ("PR", item.head_passes)):
            first = " / ".join(f"{p.samples_ms[0]:.3f}" for p in passes)
            early = " / ".join(f"{statistics.median(p.samples_ms[:p.late_start]):.3f}" for p in passes)
            late = " / ".join(f"{p.late_ms:.3f}" for p in passes)
            blocks = " / ".join(", ".join(f"{v:.3f}" for v in p.late_blocks_ms) or "insufficient" for p in passes)
            drift = " / ".join(f"{(p.late_blocks_ms[-1] / p.late_blocks_ms[0] - 1) * 100:+.1f}%"
                               if p.late_blocks_ms else "—" for p in passes)
            sections.append(f"| {item.suite}/{item.workload}/{item.operation}/{item.case_id} | {variant} | "
                            f"{first} | {early} | {late} | {blocks} | {drift} |")
    sections.extend(["", "</details>", ""])

    return "\n".join(sections)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--matrix", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--summary", type=Path)
    parser.add_argument("--analysis", type=Path)
    parser.add_argument("--base-sha", required=True)
    parser.add_argument("--head-sha", required=True)
    parser.add_argument("--plank-lab-sha", required=True)
    parser.add_argument("--run-url", required=True)
    args = parser.parse_args()

    comparisons, processors = load_comparisons(args.results, args.matrix)
    report = build_report(
        comparisons, processors, args.base_sha, args.head_sha, args.plank_lab_sha, args.run_url)
    args.output.with_name(args.output.stem + "-diagnostics.md").write_text(
        build_diagnostics(comparisons) + "\n", encoding="utf-8")
    write_analysis(args.analysis or args.output.with_suffix(".json"), comparisons)
    args.output.write_text(report + "\n", encoding="utf-8")
    if args.summary:
        args.summary.write_text(report.replace(MARKER + "\n", "") + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
