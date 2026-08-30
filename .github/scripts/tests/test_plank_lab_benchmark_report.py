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
            self.assertIn("measured 3σ noise window", body)
            self.assertIn("line [100, 100]", body)
            self.assertIn("below the lower line is faster", body)
            self.assertIn("| Encoding |", body)
            self.assertIn("| Noise window |", body)
            self.assertNotIn("PR latency as % of base", body)

    def test_measured_variance_defines_noise_window(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            matrix = self.write_matrix(root)
            self.write_passes(root, "base", [10.0, 12.0], [20.0, 20.1])
            self.write_passes(root, "head", [14.0, 16.0], [22.0, 22.1])

            comparisons, _ = report.load_comparisons(root, matrix)
            by_case = {item.case_id: item for item in comparisons}

            noisy = by_case["int32-plain"]
            stable = by_case["int32-dictionary"]
            self.assertGreater(noisy.delta_percent, stable.delta_percent)
            self.assertEqual("noise", noisy.status)
            self.assertEqual("slower", stable.status)
            self.assertGreater(noisy.noise_window_percent, stable.noise_window_percent)

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
                lines.append(f"// Benchmark: {stem}PlankBenchmarks.Read: Job")
                for index, value in enumerate(values, start=1):
                    lines.append(
                        f"WorkloadResult {index}: 1 op, {value * 1_000_000:.2f} ns, {value} ms/op")
            (root / f"Synthetic-Read-{variant}-{pass_number}.log").write_text(
                "\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
