#!/usr/bin/env fish

set ROOT (realpath (dirname (status filename))/..)
set BIN $ROOT/Plank.Fuzzing.Target/bin/Release/net10.0/Plank.Fuzzing.Target
set CORPUS $ROOT/fuzz/corpus
set OUT $ROOT/fuzz/findings

# Disable core dumps
ulimit -c 0

# Kill any running fuzzers and orphaned target processes
echo "==> Killing existing fuzzers..."
pkill -9 -f afl-fuzz 2>/dev/null
pkill -9 -f Plank.Fuzzing.Target 2>/dev/null
sleep 2

# Build
echo "==> Building..."
dotnet build -c Release $ROOT/Plank.Fuzzing.Target/Plank.Fuzzing.Target.csproj \
  --verbosity minimal 2>&1 | grep -v "warning NU\|up-to-date"
or exit 1

# Preserve queue into corpus before nuking
if test -d $OUT
    echo "==> Minimizing existing queue into corpus..."
    rm -rf /tmp/afl-all-queues /tmp/afl-cmin-writer
    mkdir -p /tmp/afl-all-queues
    find $OUT -path "*/queue/id:*" -exec cp {} /tmp/afl-all-queues/ \;
    set queue_count (find /tmp/afl-all-queues -maxdepth 1 -type f | wc -l | string trim)
    if test $queue_count -gt 0
        echo "==> Found $queue_count queue items across all workers"
        env -u AFL_AUTORESUME AFL_SKIP_BIN_CHECK=1 AFL_NO_FORKSRV=1 afl-cmin -T all -i /tmp/afl-all-queues -o /tmp/afl-cmin-writer -t 5000 -- $BIN
        or exit 1
        echo "==> cmin kept "(count /tmp/afl-cmin-writer/*)" files"
        rm -rf /tmp/afl-cmin-writer /tmp/afl-all-queues
    else
        echo "==> No queue items found, skipping minimization"
        rm -rf /tmp/afl-all-queues
    end
    rm -rf $OUT
end
mkdir -p $OUT

set dump_env "DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_DbgMiniDumpName=/tmp/plank-crash-%p.dmp"
set worker_env "AFL_SKIP_BIN_CHECK=1 AFL_AUTORESUME=1 AFL_TMPDIR=/tmp $dump_env"

# Main loop: respawns workers on every restart
# Core 0: main (OOP), cores 1-19: workers, cores 20-23: free for desktop
while true
    for i in (seq 1 19)
        set name (string pad -w 2 -c 0 $i)
        if not pgrep -f "worker-$name" > /dev/null 2>&1
            fish -c "while true; env $worker_env afl-fuzz -b $i -i $CORPUS -o $OUT -t 5000 -S worker-$name -- $BIN > /dev/null 2>&1; sleep 2; end" &
            disown
        end
    end

    env AFL_SKIP_BIN_CHECK=1 AFL_AUTORESUME=1 AFL_TMPDIR=/tmp $dump_env afl-fuzz -b 0 -i $CORPUS -o $OUT -t 5000 -M main -- $BIN > /dev/null 2>&1
    echo "==> main crashed, restarting in 2s..."
    sleep 2
end
