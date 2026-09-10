import json
import sys
import tempfile
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import plank_lab_benchmark_report as report


class PlankLabBenchmarkReportTests(unittest.TestCase):
    def test_report_uses_matrix_and_benchmarkdotnet_samples(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            matrix = self.write_matrix(root)
            self.write_passes(root, "base", [10.0, 10.2], [20.0, 20.2])
            self.write_passes(root, "head", [8.0, 8.2], [22.0, 22.2])

            comparisons, processors = report.load_comparisons(root, matrix)
            body = report.build_report(
                comparisons, processors, "a" * 40, "b" * 40, "c" * 40,
                "https://github.com/Kuinox/Plank/actions/runs/1")

            self.assertEqual(2, len(comparisons))
            self.assertIn("Plank-Lab published matrix", body)
            self.assertIn("🟢 −19.8%", body)
            self.assertIn("🔴 +10.0%", body)
            self.assertIn("```mermaid", body)
            self.assertIn("int32 · plain", body)
            self.assertIn("xychart-beta horizontal", body)
            self.assertIn('y-axis "Base = 100" 75 --> 125', body)
            self.assertIn("Late-half runtime index", body)
            self.assertIn("| Observed change range |", body)
            self.assertIn("not a confidence interval", body)
            self.assertNotIn("within noise", body)

    def test_startup_spike_does_not_hide_repeatable_late_slowdown(self):
        base = report.PassSummary.create([10000] + [10] * 99)
        head = report.PassSummary.create([20000] + [12] * 99)
        item = self.comparison((base, base), (head, head))
        self.assertEqual("slower", item.status)
        self.assertAlmostEqual(20, item.delta_percent)
        self.assertEqual(10000, item.base_passes[0].samples_ms[0])

    def test_disagreeing_passes_are_inconclusive(self):
        base = report.PassSummary.create([10] * 100)
        slow = report.PassSummary.create([13] * 100)
        item = self.comparison((base, base), (slow, base))
        self.assertEqual("inconclusive", item.status)
        self.assertEqual(0, item.observed_range[0])
        self.assertAlmostEqual(30, item.observed_range[1])

    def test_late_transition_is_not_hidden_by_median(self):
        base = report.PassSummary.create([10] * 100)
        transition = report.PassSummary.create([20] * 80 + [8] * 20)
        item = self.comparison((base, base), (transition, transition))
        self.assertEqual("inconclusive", item.status)
        self.assertEqual((20, 20, 20, 8, 8), transition.late_blocks_ms)

    def test_short_runs_do_not_claim_equivalence_or_direction(self):
        short = report.PassSummary.create([1, 2])
        item = self.comparison((short, short), (short, short))
        self.assertEqual("insufficient", item.status)
        self.assertIsNone(item.observed_range)

    def test_all_cross_pass_comparisons_are_used(self):
        passes = [report.PassSummary.create([v] * 100) for v in (10, 20, 11, 22)]
        item = self.comparison(tuple(passes[:2]), tuple(passes[2:]))
        self.assertEqual("inconclusive", item.status)

    @staticmethod
    def comparison(base, head):
        return report.Comparison("synthetic", "read", "case", "case", "int32", "plain",
                                 1000, 22, base, head)

    def test_non_plank_benchmark_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            matrix = self.write_matrix(root)
            (root / "Synthetic-Read-base-1.log").write_text(
                "// Benchmark: SyntheticInt32PlainParquetNetBenchmarks.Read: Job\n"
                "WorkloadResult 1: 1 op, 1000000.00 ns, 1 ms/op\n",
                encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "non-Plank"):
                report.load_comparisons(root, matrix)

    def test_row_and_column_results_are_kept_separate(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            matrix = self.write_matrix(root)
            self.write_passes(root, "base", [10.0, 10.0], [20.0, 20.0])
            self.write_passes(root, "head", [8.0, 8.0], [22.0, 22.0])
            originals = {}
            for path in root.glob("*.log"):
                row_text = path.read_text()
                originals[path] = row_text
                column_text = row_text.replace("PlankBenchmarks", "ColumnPlankBenchmarks")
                column_text = column_text.replace("10000000.00 ns", "30000000.00 ns")
                path.write_text(row_text + column_text)
            comparisons, processors = report.load_comparisons(root, matrix)
            self.assertEqual(4, len(comparisons))
            plain = {item.workload: item for item in comparisons
                     if item.encoding == "plain"}
            self.assertEqual(10, plain["row"].base_ms)
            self.assertEqual(30, plain["column"].base_ms)
            body = report.build_report(comparisons, processors, "a", "b", "c", "url")
            self.assertIn("Synthetic · Row · Read", body)
            self.assertIn("Synthetic · Column · Read", body)
            for path in root.glob("*-head-*.log"):
                path.write_text(originals[path])
            with self.assertRaisesRegex(ValueError, "Missing base or head"):
                report.load_comparisons(root, matrix)

    def test_empty_benchmark_log_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            matrix = self.write_matrix(root)
            (root / "Synthetic-Read-base-1.log").write_text("Build failed")
            with self.assertRaisesRegex(ValueError, "contains no Plank-Lab benchmarks"):
                report.load_comparisons(root, matrix)

    def test_missing_pass_and_truncated_series_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            matrix = self.write_matrix(root)
            self.write_passes(root, "base", [10, 10], [20, 20])
            self.write_passes(root, "head", [12, 12], [22, 22])
            path = root / "Synthetic-Read-head-2.log"
            text = path.read_text()
            path.write_text(text.rsplit("WorkloadActual", 1)[0])
            with self.assertRaisesRegex(ValueError, "Incomplete measurements"):
                report.load_comparisons(root, matrix)
            path.unlink()
            with self.assertRaisesRegex(ValueError, "Missing base or head pass"):
                report.load_comparisons(root, matrix)

    def test_raw_order_operation_normalization_and_result_duplicates(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            matrix = self.write_matrix(root)
            self.write_passes(root, "base", [10, 20], [20, 20])
            self.write_passes(root, "head", [12, 12], [22, 22])
            for path in root.glob("*.log"):
                text = path.read_text().replace("1 op,", "2 op,")
                path.write_text(text + "WorkloadResult 1: 1 op, 99999 ns, 1 ms/op\n")
            items, _ = report.load_comparisons(root, matrix)
            self.assertEqual((5, 10) * 50, items[0].base_passes[0].samples_ms)
            report.write_analysis(root / "analysis.json", items)
            data = json.loads((root / "analysis.json").read_text())
            self.assertEqual([5, 10] * 50, data[0]["base_passes"][0]["samples_ms"])
            path = root / "Synthetic-Read-head-2.log"
            path.write_text(path.read_text().replace("WorkloadActual 2:", "WorkloadActual 3:"))
            with self.assertRaisesRegex(ValueError, "Non-contiguous"):
                report.load_comparisons(root, matrix)

    def test_saved_pr_artifacts_preserve_pass_disagreement_and_late_direction(self):
        path = Path(__file__).parent / "fixtures/pr103-pr104-ordered.json"
        for case in json.loads(path.read_text()):
            with self.subTest(pr=case["pr"], case=case["case_id"]):
                base = tuple(report.PassSummary.create(p) for p in case["base"])
                head = tuple(report.PassSummary.create(p) for p in case["head"])
                item = self.comparison(base, head)
                self.assertEqual(case["expected_status"], item.status)
                self.assertAlmostEqual(case["expected_delta"], item.delta_percent)
                self.assertEqual(400, sum(len(p.samples_ms) for p in base + head))
                self.assertEqual(50, base[0].late_start)
                if case["pr"] == 103 and case["case_id"] == "int32-delta-binary-packed-column":
                    self.assertGreater(head[0].late_ms, 30)
                    self.assertLess(head[1].late_ms, 24)
                    self.assertEqual("inconclusive", item.status)
                if case["case_id"] == "int64-delta-binary-packed-column" and case["suite"] == "synthetic":
                    self.assertLess(abs(item.delta_percent), 1.1)

    @staticmethod
    def write_matrix(root: Path) -> Path:
        path = root / "matrix.json"
        path.write_text(json.dumps([
            {
                "suite": "synthetic", "id": "int32-plain", "stem": "SyntheticInt32Plain",
                "label": "int32 · plain", "encoding": "plain", "dataTypes": ["int32"],
                "rowCount": 1000, "columnCount": 22,
            },
            {
                "suite": "synthetic", "id": "int32-dictionary",
                "stem": "SyntheticInt32Dictionary", "label": "int32 · dictionary",
                "encoding": "dictionary", "dataTypes": ["int32"],
                "rowCount": 1000, "columnCount": 22,
            },
        ]), encoding="utf-8")
        return path

    @staticmethod
    def write_passes(root: Path, variant: str, plain: list[float], dictionary: list[float]):
        for pass_number in (1, 2):
            lines = ["AMD Test CPU, 1 CPU, 4 logical and 4 physical cores"]
            for stem, values in (
                    ("SyntheticInt32Plain", plain),
                    ("SyntheticInt32Dictionary", dictionary)):
                values = values * 50 if len(values) == 2 else values
                lines.append(f"// Benchmark: {stem}PlankBenchmarks.Read: Job(IterationCount={len(values)})")
                for index, value in enumerate(values, start=1):
                    lines.append(
                        f"WorkloadActual {index}: 1 op, {value * 1_000_000:.2f} ns, {value} ms/op")
            (root / f"Synthetic-Read-{variant}-{pass_number}.log").write_text(
                "\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
