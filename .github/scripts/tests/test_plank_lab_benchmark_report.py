import sys
import unittest
from dataclasses import replace
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import plank_lab_benchmark_report as report


class PlankLabBenchmarkReportTests(unittest.TestCase):
    def test_same_revision_drift_sets_robust_noise_window(self):
        comparisons = [
            self.comparison("stable-1", [100, 100.1], [100, 99.9]),
            self.comparison("stable-2", [100, 99.9], [100, 100.1]),
            self.comparison("stable-3", [100, 100.2], [100, 99.8]),
            self.comparison("outlier", [100, 150], [100, 50]),
        ]

        noise = report.estimate_noise_window_percent(comparisons)

        self.assertGreater(noise, 0.0)
        self.assertLess(noise, 1.0)

    def test_effect_must_clear_measured_noise_window(self):
        small_change = self.comparison("small", [100, 100], [103, 103])
        large_change = self.comparison("large", [100, 100], [108, 108])
        small_change = replace(small_change, noise_window_percent=5.0)
        large_change = replace(large_change, noise_window_percent=5.0)

        self.assertEqual("inconclusive", small_change.status)
        self.assertEqual("slower", large_change.status)

    def test_pr_110_unrelated_cells_remain_inconclusive(self):
        taxi_dictionary = replace(
            self.comparison("taxi-dictionary-column", [129.7, 130.1], [135.6, 134.7]),
            noise_window_percent=4.6,
        )
        int64_bss = replace(
            self.comparison("int64-byte-stream-split-column", [14.0, 13.7], [13.4, 13.5]),
            noise_window_percent=4.6,
        )

        self.assertEqual("inconclusive", taxi_dictionary.status)
        self.assertEqual("inconclusive", int64_bss.status)

    def test_report_displays_measured_noise_window(self):
        comparison = replace(
            self.comparison("small", [100, 100], [103, 103]),
            noise_window_percent=5.0,
        )

        body = report.build_report(
            [comparison], ["Test CPU"], "a" * 40, "b" * 40, "c" * 40, "url")

        self.assertIn("measured runner noise", body)
        self.assertIn("±5.0%", body)
        self.assertIn("line [95]", body)
        self.assertIn("line [105]", body)
        self.assertIn("⚪ +3.0%", body)

    @staticmethod
    def comparison(case_id, base_late, head_late):
        return report.Comparison(
            suite="synthetic",
            operation="read",
            case_id=case_id,
            label=case_id,
            data_type="int32",
            encoding="plain",
            row_count=100,
            column_count=1,
            base_passes=tuple(PlankLabBenchmarkReportTests.pass_summary(value)
                              for value in base_late),
            head_passes=tuple(PlankLabBenchmarkReportTests.pass_summary(value)
                              for value in head_late),
        )

    @staticmethod
    def pass_summary(value):
        samples = [value] * 50
        return report.PassSummary.create(samples)


if __name__ == "__main__":
    unittest.main()
