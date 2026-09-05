# Contributing

See the [README](README.md#build-and-test) for local build and test commands.

## Running PR benchmarks

Benchmarks are opt-in. Opening, reopening, or pushing commits to a PR does not
start them.

1. Post a new PR conversation comment containing exactly `/benchmark`.
2. If you have repository write access, the run starts immediately. Otherwise,
   the bot acknowledges your request; a maintainer reviews the current commit
   and posts `/benchmark` to approve and start the run. This works for fork PRs.
3. Follow the run link posted on the PR. Once successful, the workflow posts or
   updates the comparison comment. Logs and reports are also attached to the run.

The run compares the PR's current head commit with its base commit using the
latest Plank-Lab harness. PRs must be open and target `master`.
Pushes do not automatically rerun benchmarks: post a new `/benchmark` comment
when another run is needed. Editing an existing comment does not trigger a run.
If the PR changes during a run, its report stays in the run artifacts instead of
being posted as a current comparison. A new approved request cancels older
benchmark jobs for that PR.

Maintainers can also open [Actions → PR benchmarks](https://github.com/Kuinox/Plank/actions/workflows/pr-benchmarks.yml),
choose **Run workflow**, keep the workflow branch on `master`, and enter the
**PR number**. The workflow resolves the fork and commit SHAs automatically.
For a comparison without a PR, leave the PR number empty and provide
`base_ref` and `head_ref` instead. The manual form also accepts an iteration count.

The comment command becomes available once this workflow change is merged into
`master`. Regular build and test CI continues to run automatically.
