# PR benchmark regression fixtures

`pr103-pr104-ordered.json` contains unfiltered `WorkloadActual` measurements in
milliseconds per operation from these existing GitHub Actions artifacts:

- PR #103: https://github.com/Kuinox/Plank/actions/runs/34512983316
- PR #104: https://github.com/Kuinox/Plank/actions/runs/34513008989

Matrix revision: Plank-Lab `d13142e05571c74ab9ec7c57e5fbe9e77f223d66`.
Each case retains base pass 1, base pass 2, head pass 1, head pass 2, each with
100 ordered samples. Run execution order was base 1 / head 1 / head 2 / base 2.
The selected cases cover late pass disagreement, startup/tiering differences,
and a consistent late slowdown. No new performance measurements were taken.

Full artifact replay (168 comparisons and 67,200 samples per run) gives:

| PR | Consistently faster late blocks | Inconclusive | Consistently slower late blocks |
|---|---:|---:|---:|
| 103 | 6 | 155 | 7 |
| 104 | 2 | 158 | 8 |

For #103 synthetic INT32 delta column reads, late-half base medians are
24.102 / 24.101 ms and head medians are 30.856 / 23.435 ms. Its observed block
change range is -3.5% to +30.0%, so the result is inconclusive. Pooling the
process samples previously concealed this disagreement. Synthetic INT64 delta
column reads have a late-half change of +1.0%, range -1.5% to +9.1%.

## Interpretation

The fixed late half avoids selecting a cutoff to obtain a favorable result.
Five consecutive blocks retain the time dimension while reducing sensitivity
to individual interruptions. These are descriptive choices, not a calibrated
statistical test. All cross-pass/block ratios must agree in direction for color;
there is no percent-change significance threshold. Short runs without five
blocks of at least five measurements are explicitly insufficient.

Neither separated block medians nor a small first-to-last drift proves steady
state. Late tiering and isolated outliers remain in the ordered JSON and
startup/late-block diagnostic artifact. Two process repetitions cannot support
a reliable population confidence interval; the report deliberately makes no
significance, equivalence, or sustained-throughput guarantee. Measurements of
sustained throughput need longer runs and independent repetitions when the
ordered diagnostics show continued transitions.

To replay the full artifacts, download each run separately with `gh run download`
and invoke `.github/scripts/plank_lab_benchmark_report.py` with its results
folder, the pinned matrix, and the original revision/run metadata. The normal
CLI emits the comment, ordered analysis JSON, and diagnostics Markdown; the
workflow uploads all three. The comment retains per-pass late medians and the
observed range; large ordered diagnostics stay in the artifact to respect
GitHub's comment length limit.
