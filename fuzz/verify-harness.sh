#!/usr/bin/env bash
# Pre-flight check: prove the fuzzer is actually fuzzing before committing
# hours of CPU to it.
#
# Why this exists: a fuzzer wired up wrong looks *better* than a healthy one.
# When AFL and the harness disagree about how test cases are delivered, the
# target runs on an empty input every time — and reports a spectacular exec
# rate, 100% stability and zero crashes. Two separate instances of that failure
# (SharpFuzz reading an unnegotiated shmem buffer, and AFL++ 5.x marking the
# whole bitmap as covered) went unnoticed for months.
#
# Usage: verify-harness.sh <reader|writer> [seconds]
set -euo pipefail

TARGET="${1:-reader}"
DURATION="${2:-60}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
AFL="$ROOT/fuzz/.afl/bin/afl-fuzz"

case "$TARGET" in
  reader) BIN="$ROOT/Plank.Fuzzing.Reader.Target/bin/Release/net10.0/Plank.Fuzzing.Reader.Target"
          CORPUS="$ROOT/fuzz/reader-corpus"; TIMEOUT=1100 ;;
  writer) BIN="$ROOT/Plank.Fuzzing.Target/bin/Release/net10.0/Plank.Fuzzing.Target"
          CORPUS="$ROOT/fuzz/corpus"; TIMEOUT=5000 ;;
  *) echo "unknown target '$TARGET' (expected reader or writer)" >&2; exit 2 ;;
esac

[ -x "$AFL" ] || { echo "FAIL: $AFL missing — run fuzz/install-afl.sh" >&2; exit 2; }
[ -x "$BIN" ] || { echo "FAIL: $BIN missing — build the target first" >&2; exit 2; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/tmp"

# A small seed slice keeps calibration short so the run spends its time fuzzing.
mkdir -p "$WORK/in"
n=0
for f in "$CORPUS"/*; do
  [ -f "$f" ] || continue
  cp "$f" "$WORK/in/"
  n=$((n + 1))
  [ "$n" -ge 8 ] && break
done
[ "$n" -gt 0 ] || { echo "FAIL: no seeds in $CORPUS" >&2; exit 2; }

rm -f "/tmp/plank-fuzz-$TARGET-last-input.bin" "/tmp/plank-fuzz-$TARGET-BROKEN.txt"

echo "==> Fuzzing $TARGET for ${DURATION}s to validate the harness..."
ulimit -c 0
AFL_SKIP_CPUFREQ=1 AFL_SKIP_BIN_CHECK=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1 \
  AFL_TMPDIR="$WORK/tmp" AFL_NO_UI=1 \
  timeout "$DURATION" "$AFL" -i "$WORK/in" -o "$WORK/out" -t "$TIMEOUT" -- "$BIN" \
  >"$WORK/afl.log" 2>&1 || true

STATS="$WORK/out/default/fuzzer_stats"
[ -f "$STATS" ] || { echo "FAIL: afl-fuzz produced no stats"; tail -20 "$WORK/afl.log"; exit 1; }

stat_of() { awk -F': *' -v k="$1" '$1 ~ "^"k" *$" {print $2; exit}' "$STATS" | tr -d ' '; }

EDGES=$(stat_of edges_found)
TOTAL=$(stat_of total_edges)
CVG=$(stat_of bitmap_cvg)
EXECS=$(stat_of execs_done)
CORPUS_N=$(stat_of corpus_count)

echo "    edges_found=$EDGES total_edges=$TOTAL bitmap_cvg=$CVG execs=$EXECS corpus=$CORPUS_N"

fail() { echo "FAIL: $1" >&2; exit 1; }

# 1. The harness must receive non-empty test cases.
[ -f "/tmp/plank-fuzz-$TARGET-BROKEN.txt" ] && fail "harness reported thousands of consecutive empty inputs"
LAST="/tmp/plank-fuzz-$TARGET-last-input.bin"
if [ -f "$LAST" ] && [ ! -s "$LAST" ]; then
  fail "harness is receiving empty inputs (test cases are not reaching the target)"
fi

# 2. AFL must not believe every edge is already covered. This is the AFL++ 5.x
#    signature: edges_found == total_edges == 65536 from the first execution.
[ -n "$EDGES" ] && [ -n "$TOTAL" ] || fail "could not read edge counts from fuzzer_stats"
[ "$EDGES" -ge "$TOTAL" ] && fail "edges_found ($EDGES) == total_edges ($TOTAL): AFL sees a fully-covered bitmap, so nothing can ever be interesting. Wrong AFL++ version?"

# 3. Coverage must be plausible: some edges, but nowhere near the whole map.
[ "$EDGES" -gt 100 ] || fail "only $EDGES edges found — the target is barely executing"

echo "==> OK: $TARGET harness is delivering inputs and AFL is measuring coverage."
