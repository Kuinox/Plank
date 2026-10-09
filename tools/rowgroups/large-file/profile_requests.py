"""Capture actual engine HTTP requests, with the server outside engine memory."""
import argparse
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT.parents[2] / 'Plank.Tool' / 'Querying'))
QUERY = 'SELECT "WatchID" FROM data WHERE "CounterID" = 17 AND "WatchID" = 9110818468285196899'
ENGINES = ['duckdb', 'polars', 'datafusion', 'clickhouse']
SIMULATION = dict(latency_ms=100, initial_throughput_mib_s=1.25,
                  ramp_up_ms=700, max_throughput_mib_s=53)


def worker():
    from network import adapter
    from query import fingerprint
    from types import SimpleNamespace
    request = json.loads(sys.stdin.readline())
    operation, close, version = adapter(request['engine'],
        SimpleNamespace(endpoint=request['endpoint']), request['filename'], 1)
    print(json.dumps(dict(ready=True, version=version)), flush=True)
    if sys.stdin.readline().strip() != 'GO':
        raise ValueError('Missing query start signal')
    try:
        rows = operation(QUERY)
        if fingerprint(rows) != fingerprint([(9110818468285196899,)]):
            raise ValueError('Query result differs from the expected row')
        print(json.dumps(dict(result_rows=len(rows), correct=True)), flush=True)
    finally:
        close()


def oom_count():
    path = Path('/sys/fs/cgroup/memory.events')
    if not path.exists():
        return 0
    return int(dict(line.split() for line in path.read_text().splitlines()).get('oom_kill', 0))


def capture(path, engine_name, timeout):
    from mini_s3 import MiniS3, Settings
    with MiniS3({path.name: path}, Settings(**SIMULATION)) as server, tempfile.TemporaryFile(mode='w+t') as stderr:
        child = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), '--engine-worker'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr, text=True,
            env=dict(os.environ))
        child.stdin.write(json.dumps(dict(engine=engine_name, endpoint=server.endpoint, filename=path.name)) + '\n')
        child.stdin.flush()
        line = child.stdout.readline()
        ready = json.loads(line) if line else {}
        before = oom_count()
        server.reset_clock()
        timed_out = False
        try:
            output, _ = child.communicate('GO\n' if ready else '', timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            child.kill()
            output, _ = child.communicate()
        elapsed = server.elapsed()
        if not server.wait_idle(10):
            raise TimeoutError('HTTP requests did not finish after engine exit')
        events = sorted(server.events, key=lambda e: e['time'])
        for i, event in enumerate(events):
            event['id'] = i
        item = dict(name=engine_name, version=ready.get('version', 'unknown'), requests=events,
                    elapsed_ms=elapsed, result_rows=None, result_verified=False)
        if child.returncode or timed_out:
            stderr.seek(0)
            item['error_detail'] = stderr.read()[-4000:]
            item['error'] = f'Engine exited with code {child.returncode}'
            if timed_out:
                item['error'] = f'Query exceeded {timeout:g} second timeout'
            elif oom_count() > before:
                item['error'] = 'Exceeded the environment memory limit'
            elif 'exceeds i16 max value' in item['error_detail']:
                item['error'] = 'Row group count exceeds DataFusion’s 32,768-group limit'
        else:
            outcome = json.loads(output)
            if not outcome['correct'] or not events or any(e.get('error') or e['status'] not in (200, 206) for e in events):
                raise ValueError('Invalid result or HTTP trace')
            item.update(result_rows=outcome['result_rows'], result_verified=True)
        return item


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--files', type=Path, nargs='+', required=True)
    parser.add_argument('--engines', choices=ENGINES, nargs='+', default=ENGINES)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--timeout', type=float, default=300)
    args = parser.parse_args()
    if args.output.exists():
        raise FileExistsError(args.output)
    checkpoint = args.output.with_name(args.output.name + '.incomplete')
    if checkpoint.exists():
        raise FileExistsError(checkpoint)
    result = dict(kind='rowgroups-network', version=1, transport='simulated S3 over local HTTP',
                  query=QUERY, simulation=SIMULATION, files=[])
    for path in args.files:
        path = path.resolve()
        info = json.loads(path.with_suffix('.json').read_text())
        if path.stat().st_size != info['file_bytes']:
            raise ValueError(f'Variant size differs from manifest: {path}')
        item = dict(name=path.name, target=info['target'], size=info['file_bytes'],
                    row_groups=info['row_group_count'], engines=[],
                    label=f"{info['target']:,} rows/group · {info['row_group_count']:,} groups · {info['file_bytes']/1e9:.2f} GB · simulated S3")
        result['files'].append(item)
        for engine in args.engines:
            item['engines'].append(capture(path, engine, args.timeout))
            checkpoint.write_text(json.dumps(result, indent=2) + '\n')
            print(f"{path.name}: {engine}: {item['engines'][-1].get('error', 'verified')}", flush=True)
    checkpoint.rename(args.output)


if __name__ == '__main__':
    if sys.argv[1:] == ['--engine-worker']:
        worker()
    else:
        main()
