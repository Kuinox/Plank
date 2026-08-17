#!/usr/bin/env bash
# Measure which Plank code a fuzzing corpus actually reaches.
#
# The fleet reports AFL's bitmap coverage, which counts edges in the
# instrumented binary and keeps creeping up while whole decoders sit at zero.
# It cannot answer "is ByteStreamSplit ever decoded?" — this can. It replays a
# corpus through the fuzz targets in a plain process (Plank.Fuzzing.Replay,
# which is not SharpFuzz-rewritten and so a profiler can wrap it) and reports
# per-class line coverage of the Plank assembly.
#
# Usage: coverage.sh <reader|writer|both> <corpus-dir> [<corpus-dir>...]
#
# Corpus directories may be seed corpora (fuzz/reader-corpus) or whole AFL
# findings trees (fuzz/reader-findings) — queue files are picked out of nested
# worker directories automatically.
#
# Output: fuzz/coverage/<target>.cobertura.xml plus a ranked "coldest classes"
# summary on stdout, which is the list worth attacking next.
set -euo pipefail

TARGET="${1:-reader}"
shift || true
[ "$#" -gt 0 ] || { echo "usage: coverage.sh <reader|writer|both> <corpus-dir>..." >&2; exit 2; }

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPLAY_DIR="$ROOT/Plank.Fuzzing.Replay/bin/Release/net10.0"
REPLAY="$REPLAY_DIR/Plank.Fuzzing.Replay.dll"
OUT_DIR="$ROOT/fuzz/coverage"
OUT="$OUT_DIR/$TARGET.cobertura.xml"

command -v coverlet >/dev/null 2>&1 \
  || { echo "coverlet not found — dotnet tool install --global coverlet.console" >&2; exit 2; }

echo "==> Building the replay driver..."
dotnet build -c Release "$ROOT/Plank.Fuzzing.Replay/Plank.Fuzzing.Replay.csproj" -v q --nologo >/dev/null

mkdir -p "$OUT_DIR"

# --include keeps the report to the library under test; without it the report is
# mostly BCL and ParquetSharp and the interesting rows are impossible to find.
echo "==> Replaying corpus under coverage (this takes a few minutes)..."
coverlet "$REPLAY_DIR" \
  --target dotnet \
  --targetargs "$REPLAY $TARGET $*" \
  --format cobertura \
  --output "$OUT" \
  --include "[Plank]*" \
  --include-test-assembly \
  || { echo "coverage run failed" >&2; exit 1; }

echo
echo "==> Coldest Plank classes (line coverage, lowest first)"
python3 "$ROOT/fuzz/coverage-report.py" "$OUT"
