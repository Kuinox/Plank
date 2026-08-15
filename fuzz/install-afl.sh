#!/usr/bin/env bash
# Build the pinned AFL++ into fuzz/.afl/bin.
#
# The version is pinned deliberately. Plank's persistent-mode harness
# (Plank.Fuzzing.Harness/AflPersistentHarness.cs) speaks the "old model"
# fork-server handshake. AFL++ 5.x accepts that handshake but then treats the
# whole coverage bitmap as already covered, so the corpus never grows and the
# fuzzer silently tests nothing. Do not bump this without re-running
# fuzz/verify-harness.sh.
set -euo pipefail

AFL_VERSION="${AFL_VERSION:-v4.40c}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PREFIX="$ROOT/fuzz/.afl"

if [ -x "$PREFIX/bin/afl-fuzz" ]; then
  echo "==> AFL++ already present: $("$PREFIX/bin/afl-fuzz" 2>&1 | head -1 | sed 's/\x1b\[[0-9;]*m//g')"
  exit 0
fi

echo "==> Building AFL++ $AFL_VERSION into $PREFIX"
SRC="$(mktemp -d)"
trap 'rm -rf "$SRC"' EXIT

git clone --depth 1 -b "$AFL_VERSION" https://github.com/AFLplusplus/AFLplusplus.git "$SRC"

# Build only the fuzzer binaries. We never compile a C target: .NET assemblies
# are instrumented by SharpFuzz and run under AFL_SKIP_BIN_CHECK=1. Avoiding
# 'make all' skips afl-cc and its test_build, which needs an LLVM/gcc plugin
# toolchain the servers do not have.
make -C "$SRC" -j"$(nproc)" afl-fuzz afl-showmap afl-tmin afl-analyze afl-gotcpu

mkdir -p "$PREFIX/bin"
for tool in afl-fuzz afl-showmap afl-tmin afl-analyze afl-gotcpu; do
  cp "$SRC/$tool" "$PREFIX/bin/"
done
# afl-cmin/afl-whatsup are scripts; corpus minimisation uses them.
for script in afl-cmin afl-cmin.bash afl-whatsup afl-plot; do
  [ -f "$SRC/$script" ] && install -m 755 "$SRC/$script" "$PREFIX/bin/"
done
[ -f "$SRC/afl-cmin.awk" ] && cp "$SRC/afl-cmin.awk" "$PREFIX/bin/"

echo "==> Installed: $("$PREFIX/bin/afl-fuzz" 2>&1 | head -1 | sed 's/\x1b\[[0-9;]*m//g')"
