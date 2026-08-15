#!/usr/bin/env bash
# Replay every saved crash input and group them by exception signature.
#
# The fuzzers save raw inputs (fuzz/crashes/<target>-<hash>.bin); hundreds of
# them usually collapse to a handful of distinct defects. Output is one block
# per signature, with a representative input to reproduce from.
#
# Usage: triage.sh [crash_dir]
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIR="${1:-$ROOT/fuzz/crashes}"
READER="$ROOT/Plank.Fuzzing.Reader.Target/bin/Release/net10.0/Plank.Fuzzing.Reader.Target"
WRITER="$ROOT/Plank.Fuzzing.Target/bin/Release/net10.0/Plank.Fuzzing.Target"

[ -d "$DIR" ] || { echo "no crash directory at $DIR"; exit 0; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

count=0
for f in "$DIR"/*.bin; do
  [ -f "$f" ] || continue
  case "$(basename "$f")" in
    reader-*) BIN="$READER" ;;
    writer-*) BIN="$WRITER" ;;
    *) continue ;;
  esac

  out=$("$BIN" < "$f" 2>&1)
  [ -z "$out" ] && continue   # no longer reproduces

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
