#!/usr/bin/env fish

set ROOT (realpath (dirname (status filename))/..)
set BIN $ROOT/Plank.Fuzzing.Reader.Target/bin/Release/net10.0/Plank.Fuzzing.Reader.Target
set CORPUS $ROOT/fuzz/reader-corpus
set OUT $ROOT/fuzz/reader-findings

# Preserve queue into corpus before nuking
if test -d $OUT
    echo "==> Minimizing existing queue into corpus..."
    afl-cmin -i $OUT -o /tmp/afl-cmin-reader -t 1100 -- $BIN
    and cp /tmp/afl-cmin-reader/* $CORPUS/
    and rm -rf /tmp/afl-cmin-reader
    and echo "==> Corpus updated: "(count $CORPUS/*)" seeds"
end

# Kill any running fuzzers and orphaned target processes
echo "==> Killing existing fuzzers..."
pkill -9 -f afl-fuzz 2>/dev/null
pkill -9 -f Plank.Fuzzing.Reader.Target 2>/dev/null
sleep 2

# Build
echo "==> Building..."
dotnet build -c Release $ROOT/Plank.Fuzzing.Reader.Target/Plank.Fuzzing.Reader.Target.csproj \
  --verbosity minimal 2>&1 | grep -v "warning NU\|up-to-date"
or exit 1

# Fresh output dir
rm -rf $OUT
mkdir -p $OUT

# Launch
echo "==> Starting 24 workers..."
for i in (seq 1 23)
    set name (string pad -w 2 -c 0 $i)
    AFL_SKIP_BIN_CHECK=1 afl-fuzz -b $i -i $CORPUS -o $OUT -t 1100 -S worker-$name -- $BIN &
    disown
end

AFL_SKIP_BIN_CHECK=1 afl-fuzz -b 0 -i $CORPUS -o $OUT -t 1100 -M main -- $BIN
