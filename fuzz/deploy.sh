#!/usr/bin/env bash
# Roll the shared fuzzing branch out to every machine in fuzz/hosts.conf and
# restart their fleets.
#
# Usage: deploy.sh [branch] [--local-only|--remote-only]
#
# Order matters, and getting it wrong fabricates crashes. The workers execute
# the built assemblies directly, so rebuilding while a fleet is running lets a
# worker load a half-written Plank.dll, throw something the target does not
# expect, and save the input as a crash. Two such files showed up the first time
# this was done by hand — both large, valid-looking inputs that reproduce on no
# binary at all, which costs a triage cycle each to dismiss. So: stop, build,
# reseed, start.
#
# The findings directories are never touched. They hold the queue, which is the
# real corpus — tens of thousands of inputs representing days of CPU — and AFL
# resumes from it. Only the generated seeds are replaced, because those have to
# track the code that generates them.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BRANCH="${1:-fuzz/aug2026}"
[ "${BRANCH#-}" != "$BRANCH" ] && BRANCH="fuzz/aug2026"   # first arg was a flag
SCOPE="all"
for arg in "$@"; do
  case "$arg" in
    --local-only) SCOPE="local" ;;
    --remote-only) SCOPE="remote" ;;
  esac
done

HOSTS_FILE="${PLANK_FUZZ_HOSTS:-$ROOT/fuzz/hosts.conf}"
REMOTE_ROOT="${PLANK_FUZZ_REMOTE_ROOT:-fuzz/Plank}"

# The body is identical on every machine including this one, so it lives in one
# place and runs either through ssh or through bash directly.
read -r -d '' REMOTE_SCRIPT <<'REMOTE' || true
set -euo pipefail
cd "$PLANK_ROOT"

# Non-interactive ssh reads no profile, so a machine whose toolchain lives
# somewhere non-standard has to say so in env.conf. Without this the build fails
# with "sharpfuzz: command not found" and, worse, the *old* binaries are left in
# place — so the fleet restarts happily on stale code and reports healthy stats.
[ -f fuzz/env.conf ] && . fuzz/env.conf

git fetch origin --quiet
git checkout "$PLANK_BRANCH" --quiet 2>/dev/null || git checkout -B "$PLANK_BRANCH" "origin/$PLANK_BRANCH" --quiet
git reset --hard "origin/$PLANK_BRANCH" --quiet
# reset --hard does not touch submodules, so the parquet-testing checkout has to
# be moved to the commit this branch pins on its own. The seeds derived from it
# below are not in the repository, so a stale or missing checkout silently costs
# the reader fleet every envelope Plank's own writer cannot produce.
git submodule sync --quiet
git submodule update --init --recursive --quiet
echo "  at $(git log --oneline -1)"

echo "  stopping fleets..."
pkill -9 -f afl-fuzz 2>/dev/null || true
pkill -9 -f Plank.Fuzzing 2>/dev/null || true
# The crash notifier matches neither pattern above -- its command line names
# notify-crashes.sh -- so it used to survive every rollout and keep probing the
# target while the build rewrote Plank.dll underneath it, reporting the resulting
# BadImageFormatException as a crash. run-fleet.sh starts a fresh one at the end.
pkill -9 -f PLANK_FUZZ_NOTIFIER_LOOP 2>/dev/null || true
sleep 2

# kill -9 leaves each worker's .cur_input behind, and AFL refuses to start with
# one present: "AFL_TMPDIR already has an existing temporary input file". The
# respawn loop retries five seconds later so the fleet recovers, but every
# restart burns a cycle per worker and logs a PROGRAM ABORT that looks identical
# to a real one -- 16 of them, once, buried the abort log during one rollout.
rm -f fuzz/.tmp/*/.cur_input

for project in Plank.Fuzzing.Reader.Target Plank.Fuzzing.Target; do
  if ! output=$(dotnet build -c Release "$project/$project.csproj" -v q --nologo 2>&1); then
    echo "  BUILD FAILED: $project" >&2
    printf '%s\n' "$output" | grep -E 'error' | head -5 >&2
    exit 1
  fi
done
echo "  built both targets"

# Regenerated rather than merged: a stale seed can encode a selector byte that
# no longer means what it did, which silently retargets part of the corpus.
rm -f fuzz/reader-corpus/gen-*.bin
./Plank.Fuzzing.Reader.Target/bin/Release/net10.0/Plank.Fuzzing.Reader.Target \
  --generate-corpus fuzz/reader-corpus 2>/dev/null | sed 's/^/  /'

# Same reasoning, from the other source: these come out of the submodule rather
# than out of the writer, and copying them into the repository would vendor a
# corpus that is already pinned by commit.
bash fuzz/import-parquet-testing-seeds.sh | sed 's/^/  /'

# The cross-writer seeds are choice indexes for the plan decoder, so they are
# written by the code that decodes them rather than kept as bytes nobody can
# read or reproduce.
rm -f fuzz/corpus/crosswriter-*
./Plank.Fuzzing.Target/bin/Release/net10.0/Plank.Fuzzing.Target \
  --generate-corpus fuzz/corpus 2>/dev/null | sed 's/^/  /'

bash fuzz/run-fleet.sh reader 2>&1 | grep -E '==> (Starting|Running)' | sed 's/^/  /'
bash fuzz/run-fleet.sh writer 2>&1 | grep -E '==> (Starting|Running)' | sed 's/^/  /'
REMOTE

if [ "$SCOPE" != "remote" ]; then
  echo "== $(cat "$ROOT/fuzz/label.conf" 2>/dev/null || echo local)"
  # Fed on stdin, not as `bash -c "$REMOTE_SCRIPT"`. With -c the whole script
  # text becomes the shell's command line, so the script's own pkill pattern
  # matches the shell running it and the deploy kills itself mid-run — which is
  # exactly what happened the first time, locally, while both ssh runs survived
  # because they were already reading from stdin.
  if ! PLANK_ROOT="$ROOT" PLANK_BRANCH="$BRANCH" bash -s <<<"$REMOTE_SCRIPT"; then
    echo "  !! local failed" >&2
  fi
fi

if [ "$SCOPE" = "local" ]; then
  exit 0
fi

if [ ! -f "$HOSTS_FILE" ]; then
  echo "No machine list at $HOSTS_FILE — see collect-crashes.sh for its format." >&2
  exit 2
fi

while IFS='|' read -r name target identity; do
  case "${name// /}" in ''|\#*) continue ;; esac
  identity="${identity/#\~/$HOME}"
  echo "== $name"
  # PLANK_ROOT/PLANK_BRANCH are passed in the command rather than with SendEnv,
  # which needs sshd to accept them.
  if ! ssh -o BatchMode=yes -o ConnectTimeout=20 -i "$identity" "$target" \
      "PLANK_ROOT='$REMOTE_ROOT' PLANK_BRANCH='$BRANCH' bash -s" <<<"$REMOTE_SCRIPT"; then
    echo "  !! $name failed" >&2
  fi
done < "$HOSTS_FILE"
