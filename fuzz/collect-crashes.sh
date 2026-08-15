#!/usr/bin/env bash
# Pull crash inputs off the remote fuzzing machines into fuzz/crashes.
#
# Filenames are content hashes, so collecting repeatedly is a no-op and the
# same crash found on two machines collapses to one file.
#
# Machines are listed in fuzz/hosts.conf, which is not tracked — it describes
# your infrastructure, not the project. One machine per line:
#
#     name|ssh-target|identity-file
#     builder|user@host.example|~/.ssh/id_ed25519
#
# Blank lines and lines starting with # are ignored. The remote is expected to
# have the repository checked out at REMOTE_ROOT (default ~/fuzz/Plank).
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="$ROOT/fuzz/crashes"
HOSTS_FILE="${PLANK_FUZZ_HOSTS:-$ROOT/fuzz/hosts.conf}"
REMOTE_ROOT="${PLANK_FUZZ_REMOTE_ROOT:-fuzz/Plank}"

mkdir -p "$DEST"

if [ ! -f "$HOSTS_FILE" ]; then
  cat >&2 <<EOF
No machine list at $HOSTS_FILE.

Create it with one machine per line:

    name|ssh-target|identity-file

for example:

    builder|user@host.example|~/.ssh/id_ed25519

It is gitignored: it describes your infrastructure, not the project.
EOF
  exit 2
fi

before=$(find "$DEST" -name '*.bin' | wc -l)

while IFS='|' read -r name target key; do
  case "${name## }" in ''|\#*) continue ;; esac
  [ -n "${target:-}" ] && [ -n "${key:-}" ] || { echo "!! $name: malformed line, skipping" >&2; continue; }
  key="${key/#\~/$HOME}"

  if [ ! -f "$key" ]; then
    echo "!! $name: identity $key missing, skipping" >&2
    continue
  fi
  # ssh refuses to use a key that others can read.
  chmod 600 "$key" 2>/dev/null

  # -n matters: without it ssh reads the rest of the host list off stdin and
  # the loop silently stops after the first machine.
  opts=(-i "$key" -o StrictHostKeyChecking=accept-new -o ConnectTimeout=15)
  # -n matters for ssh: without it ssh reads the rest of the host list off
  # stdin and the loop silently stops after the first machine. Do not pass it
  # to scp, where -n means dry-run.
  n=$(ssh -n "${opts[@]}" "$target" "ls $REMOTE_ROOT/fuzz/crashes/*.bin 2>/dev/null | wc -l" 2>/dev/null)
  if [ -z "$n" ] || [ "$n" = "0" ]; then
    echo "== $name: no crashes"
    continue
  fi

  echo "== $name: $n crash inputs"
  rsync -a -e "ssh ${opts[*]}" "$target:$REMOTE_ROOT/fuzz/crashes/" "$DEST/" </dev/null 2>/dev/null \
    || scp -q "${opts[@]}" "$target:$REMOTE_ROOT/fuzz/crashes/*.bin" "$DEST/" </dev/null 2>/dev/null \
    || echo "!! $name: transfer failed" >&2
done < "$HOSTS_FILE"

after=$(find "$DEST" -name '*.bin' | wc -l)
echo "==> $DEST: $before -> $after crash inputs"
