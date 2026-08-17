#!/usr/bin/env bash
# Replay every saved crash input and group them by exception signature.
#
# The fuzzers save raw inputs (fuzz/crashes/<target>-<hash>.bin); hundreds of
# them usually collapse to a handful of distinct defects. Output is one block
# per signature, with a representative input to reproduce from.
#
# Usage: triage.sh [--retire] [crash_dir]
#
# --retire moves inputs that no longer reproduce into fuzz/crashes/retired/.
# They are kept, not deleted: an input that stopped reproducing is evidence a
# fix worked, and one that starts again is worth knowing about.
#
# Retiring matters because nothing ever removed them. The desktop accumulated
# 219 inputs that reproduce on no commit, and they cost real time: triage spawns
# a process per input, the crash notifier re-tests every one of them each time
# the commit changes (deliberately — a fix that did not work should re-report),
# and every "how many crashes do we have" answer is inflated by them. Tracking
# down one genuine notification meant first proving 219 dead inputs were dead.
set -uo pipefail

RETIRE=0
args=()
for arg in "$@"; do
  case "$arg" in
    --retire) RETIRE=1 ;;
    *) args+=("$arg") ;;
  esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIR="${args[0]:-$ROOT/fuzz/crashes}"
READER="$ROOT/Plank.Fuzzing.Reader.Target/bin/Release/net10.0/Plank.Fuzzing.Reader.Target"
WRITER="$ROOT/Plank.Fuzzing.Target/bin/Release/net10.0/Plank.Fuzzing.Target"

[ -d "$DIR" ] || { echo "no crash directory at $DIR"; exit 0; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

count=0
retired=0
for f in "$DIR"/*.bin; do
  [ -f "$f" ] || continue
  case "$(basename "$f")" in
    reader-*) BIN="$READER" ;;
    writer-*) BIN="$WRITER" ;;
    *) continue ;;
  esac

  out=$("$BIN" < "$f" 2>&1)
  if [ -z "$out" ]; then
    # No longer reproduces.
    if [ "$RETIRE" = 1 ]; then
      mkdir -p "$DIR/retired"
      mv "$f" "$DIR/retired/"
      retired=$((retired + 1))
    fi
    continue
  fi

  # Signature: exception type + first Plank frame it unwound through.
  extype=$(printf '%s' "$out" | grep -oE '^Unhandled exception\. [A-Za-z0-9_.]+' | head -1 | sed 's/^Unhandled exception\. //')
  frame=$(printf '%s' "$out" | grep -oE 'at Plank\.[A-Za-z0-9_.`<>+]+' | head -1 | sed 's/^at //')
  [ -z "$extype" ] && extype="(no exception header)"
  [ -z "$frame" ] && frame="(no Plank frame)"
  sig="$extype|$frame"

  slug=$(printf '%s' "$sig" | tr -c 'A-Za-z0-9' '_')
  if [ ! -f "$WORK/$slug.first" ]; then
    printf '%s' "$out" > "$WORK/$slug.first"
    printf '%s' "$f" > "$WORK/$slug.file"
    printf '%s' "$sig" > "$WORK/$slug.sig"
  fi
  echo "$f" >> "$WORK/$slug.list"
  count=$((count + 1))
done

echo "==> $count crash inputs still reproduce, grouped into $(ls "$WORK"/*.sig 2>/dev/null | wc -l) signatures"
[ "$retired" -gt 0 ] && echo "==> retired $retired input(s) that no longer reproduce to $DIR/retired/"
if [ "$RETIRE" = 0 ] && [ "$count" -eq 0 ]; then
  echo "    (re-run with --retire to move the dead ones aside)"
fi
echo

for s in "$WORK"/*.sig; do
  [ -f "$s" ] || continue
  slug="${s%.sig}"
  n=$(wc -l < "$slug.list")
  echo "─────────────────────────────────────────────────────────────"
  echo "[$n inputs] $(cat "$s")"
  echo "  repro: $(cat "$slug.file")"
  head -6 "$slug.first" | sed 's/^/  | /'
  echo
done
