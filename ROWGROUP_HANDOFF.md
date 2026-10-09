# Row-group experiment handoff

The complete context, results, recovery locations and tested rebuild commands
are in [tools/rowgroups/large-file/HANDOFF.md](tools/rowgroups/large-file/HANDOFF.md).

Nine layouts of the 99,997,497-row ClickBench dataset were generated and tested
with four engines. Saved request traces include 33 verified successful queries
and three labelled failures on the 1,000-row layout. Large data is not in Git;
the checksummed saved parts and a public-source regeneration path are documented.

Recover the finished report without downloading data or installing engines:

```sh
python tools/rowgroups/large-file/rebuild_report.py \
  --output /tmp/large-file-tool-report.html
```

The previous scratch files and virtual environments were lost. This branch
contains the recovered captures, repacker and portable reconstruction scripts;
do not assume paths from the old conversation exist.
