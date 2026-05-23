#!/usr/bin/env fish

set ROOT (realpath (dirname (status filename))/..)
set BIN $ROOT/Plank.Fuzzing.Reader.Target/bin/Release/net10.0/Plank.Fuzzing.Reader.Target
set CORPUS $ROOT/fuzz/reader-corpus
set OUT $ROOT/fuzz/reader-findings

# --oop flag: run workers in OOP mode (default: main=OOP, workers=persistent/inline)
set oop 0
if contains -- --oop $argv
    set oop 1
    echo "==> Mode: OutOfProcess everywhere (crash-safe)"
else
    echo "==> Mode: main=OOP (stable), workers=persistent (CPU-pinned, ~10x faster)"
end

# Disable core dumps — system cores from kill -9 were filling /tmp
ulimit -c 0

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

# Preserve queue into corpus before nuking
if test -d $OUT
    echo "==> Minimizing existing queue into corpus..."
    rm -rf /tmp/afl-all-queues /tmp/afl-cmin-reader
    mkdir -p /tmp/afl-all-queues
    find $OUT -path "*/queue/id:*" -exec cp {} /tmp/afl-all-queues/ \;
    set queue_count (find /tmp/afl-all-queues -maxdepth 1 -type f | wc -l | string trim)
    if test $queue_count -gt 0
        echo "==> Found $queue_count queue items across all workers"
        env -u AFL_AUTORESUME AFL_SKIP_BIN_CHECK=1 AFL_NO_FORKSRV=1 FUZZ_SINGLE=1 afl-cmin -T all -i /tmp/afl-all-queues -o /tmp/afl-cmin-reader -t 1100 -- $BIN
        or exit 1
        echo "==> cmin kept "(count /tmp/afl-cmin-reader/*)" files"
        rm -rf /tmp/afl-cmin-reader /tmp/afl-all-queues
    else
        echo "==> No queue items found, skipping minimization"
        rm -rf /tmp/afl-all-queues
    end
    rm -rf $OUT
end
mkdir -p $OUT

# Type=1 (mini) instead of 3 (full heap) — full dumps were filling /tmp (32GB tmpfs)
set dump_env "DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_DbgMiniDumpName=/tmp/plank-crash-%p.dmp"

set worker_env "AFL_SKIP_BIN_CHECK=1 AFL_AUTORESUME=1 AFL_TMPDIR=/tmp $dump_env"
if test $oop -eq 1
    set worker_env "AFL_SKIP_BIN_CHECK=1 AFL_AUTORESUME=1 AFL_TMPDIR=/tmp FUZZ_OOP=1 $dump_env"
end

# Main loop: respawns workers on every restart so pkill can't permanently kill them
while true
    # Ensure all workers are running (starts missing ones, ignores already-running)
    # Workers on cores 1-21; core 0 (main/OOP, mostly idle) + cores 22-23 free for desktop
    for i in (seq 1 19)
        set name (string pad -w 2 -c 0 $i)
        if not pgrep -f "worker-$name" > /dev/null 2>&1
            fish -c "while true; nice -n 19 env $worker_env afl-fuzz -b $i -i $CORPUS -o $OUT -t 1100 -S worker-$name -- $BIN > /dev/null 2>&1; sleep 2; end" &
            disown
        end
    end

    nice -n 19 env AFL_SKIP_BIN_CHECK=1 AFL_AUTORESUME=1 AFL_TMPDIR=/tmp FUZZ_OOP=1 $dump_env afl-fuzz -b 0 -i $CORPUS -o $OUT -t 1100 -M main -- $BIN > /dev/null 2>&1
    echo "==> main crashed, restarting in 2s..."
    sleep 2
end
