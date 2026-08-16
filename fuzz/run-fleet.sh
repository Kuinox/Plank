#!/usr/bin/env bash
# Start a fleet of AFL++ workers for one target, in the background, surviving
# logout. Portable bash so the same script runs on the desktop and on the
# servers (which have no fish).
#
# Usage: run-fleet.sh <reader|writer> [worker_count]
#
# Workers are niced so the box stays usable when it has other work to do.
# Findings land in fuzz/<target>-findings/ and are never deleted by this
# script — collect-crashes.sh pulls them.
set -euo pipefail

TARGET="${1:-reader}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Workers run under setsid, which does not read .bashrc — so a machine whose
# toolchain lives somewhere non-standard (a private DOTNET_ROOT, say) must say
# so here rather than in a login profile. Untracked: this is machine setup, not
# project detail. Without it such a fleet starts and every worker immediately
# dies with "You must install .NET to run this application".
# shellcheck source=/dev/null
[ -f "$ROOT/fuzz/env.conf" ] && . "$ROOT/fuzz/env.conf"

AFL="$ROOT/fuzz/.afl/bin/afl-fuzz"

case "$TARGET" in
  reader) BIN="$ROOT/Plank.Fuzzing.Reader.Target/bin/Release/net10.0/Plank.Fuzzing.Reader.Target"
          CORPUS="$ROOT/fuzz/reader-corpus"; TIMEOUT=1100 ;;
  writer) BIN="$ROOT/Plank.Fuzzing.Target/bin/Release/net10.0/Plank.Fuzzing.Target"
          CORPUS="$ROOT/fuzz/corpus"; TIMEOUT=5000 ;;
  *) echo "unknown target '$TARGET' (expected reader or writer)" >&2; exit 2 ;;
esac

WORKERS="${2:-$(( $(nproc) - 2 ))}"
[ "$WORKERS" -lt 1 ] && WORKERS=1

OUT="$ROOT/fuzz/$TARGET-findings"
LOGS="$ROOT/fuzz/logs/$TARGET"
mkdir -p "$OUT" "$LOGS"

[ -x "$AFL" ] || { echo "run fuzz/install-afl.sh first" >&2; exit 2; }
[ -x "$BIN" ] || { echo "build the $TARGET target first" >&2; exit 2; }

# The reader's generated seeds are derived from the writer, not checked in: they
# are regenerable binaries that must track the code. Without them the fuzzer
# only ever sees uncompressed files of a few types, and no amount of mutation
# invents a valid Snappy or Zstd frame plus the matching codec field — those
# decoders simply never run.
if [ "$TARGET" = "reader" ] && ! ls "$CORPUS"/gen-*.bin >/dev/null 2>&1; then
  echo "==> Generating reader seed corpus..."
  "$BIN" --generate-corpus "$CORPUS" || echo "!! seed generation failed; continuing with checked-in seeds"
fi

echo "==> Stopping any existing $TARGET fuzzers..."
pkill -9 -f "$OUT" 2>/dev/null || true
pkill -9 -f "$(basename "$BIN")" 2>/dev/null || true
sleep 2

ulimit -c 0

# AFL refuses to start when core dumps are piped to a helper, because the pipe
# delays crash reporting enough to look like a timeout.
if grep -q '^|' /proc/sys/kernel/core_pattern 2>/dev/null; then
  echo "core" | sudo tee /proc/sys/kernel/core_pattern >/dev/null 2>&1 \
    || echo "!! could not set core_pattern; falling back to AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES"
fi

export AFL_SKIP_CPUFREQ=1 AFL_SKIP_BIN_CHECK=1 AFL_AUTORESUME=1 AFL_NO_UI=1
export AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1
# Mini dumps only: full heap dumps of a .NET process fill /tmp within minutes.
export DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1
export DOTNET_DbgMiniDumpName="/tmp/plank-$TARGET-crash-%p.dmp"
# The persistent harness *is* the fork server, so a crash kills the worker and
# AFL aborts before saving anything. The harness writes the offending input here
# instead, keyed by content hash.
export PLANK_FUZZ_CRASH_DIR="$ROOT/fuzz/crashes"
mkdir -p "$PLANK_FUZZ_CRASH_DIR"

start_one() {
  local name="$1" core="$2"
  local tmp="$ROOT/fuzz/.tmp/$name"
  mkdir -p "$tmp"
  # Each worker needs its own AFL_TMPDIR: they all write .cur_input there.
  # Respawn loop keeps the fleet alive across the occasional worker abort.
  nohup setsid bash -c "
    while true; do
      AFL_TMPDIR='$tmp' nice -n 19 '$AFL' -b $core -i '$CORPUS' -o '$OUT' -t $TIMEOUT $3 -- '$BIN' 2>&1 \
        | grep --line-buffered -v 'Fuzzing test case #' >>'$LOGS/$name.log'
      # AFL_NO_UI reprints a status line continuously, which is ~1.2 GiB per
      # worker per 10 hours — enough to fill a small disk overnight while
      # telling us nothing. Everything else (warnings, aborts, crash notices)
      # still reaches the log.
      sleep 5
    done" >/dev/null 2>&1 &
  disown || true
}

echo "==> Starting main + $((WORKERS - 1)) workers for '$TARGET' (out: $OUT)"
start_one "main" 0 "-M main"
for i in $(seq 1 $((WORKERS - 1))); do
  name=$(printf "worker-%02d" "$i")
  start_one "$name" "$i" "-S $name"
  sleep 0.2
done

sleep 5
echo "==> Running: $(pgrep -fc "$OUT" || echo 0) afl-fuzz processes"

# A machine that finds something should say so without anyone watching it.
# Same lifecycle as the workers: no cron, no service, gone when the fleet is
# killed. Guarded so starting the reader and writer fleets leaves one notifier.
if [ -f "$ROOT/fuzz/webhook.conf" ] || [ -n "${PLANK_FUZZ_WEBHOOK:-}" ]; then
  # Match on a marker rather than the script path: the supervisor's command
  # line quotes that path, so a path-based pattern never matches and every
  # fleet start would add another notifier.
  if pgrep -f PLANK_FUZZ_NOTIFIER_LOOP=1 >/dev/null 2>&1; then
    echo "==> Crash notifier already running"
  else
    nohup setsid bash -c "
      export PLANK_FUZZ_NOTIFIER_LOOP=1
      while true; do
        '$ROOT/fuzz/notify-crashes.sh' --loop >>'$ROOT/fuzz/logs/notify.log' 2>&1
        sleep 300
      done" >/dev/null 2>&1 &
    disown || true
    echo "==> Crash notifier started (every 5 min)"
  fi
else
  echo "==> No fuzz/webhook.conf — crash notifier not started"
fi
