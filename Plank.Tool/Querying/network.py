"""Run SQL against the local simulator and produce the linked request viewer."""
import importlib.metadata
import json
import os
import sys
import time
from pathlib import Path
from urllib.parse import quote

from mini_s3 import MiniS3, Settings
from query import fingerprint


def adapter(engine, server, filename, threads):
    # The simulator is loopback-only; retain proxy settings for all remote hosts.
    for name in ('NO_PROXY', 'no_proxy'):
        existing = os.environ.get(name, '')
        os.environ[name] = existing + (',' if existing else '') + '127.0.0.1,localhost'
    url = server.endpoint + '/plank/' + quote(filename)
    version = importlib.metadata.version({'duckdb':'duckdb', 'polars':'polars',
                                         'datafusion':'datafusion', 'clickhouse':'chdb'}[engine])
    if engine == 'duckdb':
        import duckdb
        context = duckdb.connect()
        try:
            context.execute('LOAD httpfs')
        except duckdb.Error:
            # httpfs is downloaded on demand rather than bundled in the tool.
            context.execute("SET custom_extension_repository='https://extensions.duckdb.org'")
            context.execute('INSTALL httpfs')
            context.execute('LOAD httpfs')
        context.execute(f'SET threads={threads}')
        context.execute('SET enable_external_file_cache=false')
        context.execute("SET http_proxy=''")  # Only the local simulator is read by this adapter.
        def execute(query):
            context.execute("CREATE VIEW data AS SELECT * FROM read_parquet('" + url.replace("'", "''") + "')")
            return context.execute(query).fetchall()
        return execute, context.close, version
    if engine == 'polars':
        os.environ['POLARS_MAX_THREADS'] = str(threads)
        import polars as pl
        def execute(query):
            with pl.SQLContext(data=pl.scan_parquet(url, cache=False, storage_options={'max_retries':0})) as context:
                return context.execute(query).collect().rows()
        return execute, lambda: None, version
    if engine == 'datafusion':
        from datafusion import SessionConfig, SessionContext
        from datafusion.object_store import AmazonS3
        context = SessionContext(SessionConfig().with_target_partitions(threads))
        context.register_object_store('s3://', AmazonS3('plank', region='us-west-2',
            access_key_id='benchmark', secret_access_key='benchmark',
            endpoint=server.endpoint, allow_http=True))
        def execute(query):
            context.register_parquet('data', 's3://plank/' + filename)
            return [list(row.values()) for batch in context.sql(query).collect() for row in batch.to_pylist()]
        return execute, lambda: None, version
    if engine == 'clickhouse':
        from chdb import session
        context = session.Session()
        context.query(f'SET max_threads={threads}')
        context.query("SET session_timezone='UTC'")
        context.query('SET input_format_parquet_filter_push_down=1')
        context.query('SET s3_max_single_read_retries=1')
        context.query('SET http_max_tries=1')
        def execute(query):
            context.query(f"CREATE VIEW data AS SELECT * FROM s3('{url}', NOSIGN, 'Parquet')")
            return json.loads(str(context.query(query, 'JSONCompact')))['data']
        return execute, context.close, version
    raise ValueError('Simulated HTTP profiling supports duckdb, polars, datafusion and clickhouse')


def profile(variants, request):
    names = request['engines']
    if any(n not in ('duckdb', 'polars', 'datafusion', 'clickhouse') for n in names):
        raise ValueError('Simulated HTTP profiling supports duckdb, polars, datafusion and clickhouse')
    settings = Settings(**request.get('simulation', {}))
    from dataclasses import asdict
    result = dict(kind='rowgroups-network', version=1, transport='simulated S3 over local HTTP',
                  query=request['query'], simulation=asdict(settings), files=[])
    reference = None
    for variant in variants:
        path = Path(variant['variant_file'])
        filename = path.name
        item = dict(name=filename, label=f'{filename} · {variant["row_group_count"]:,} groups · simulated S3',
                    size=variant['file_size_bytes'], row_groups=variant['row_group_count'],
                    target=variant['requested_rows_per_group'], engines=[])
        for engine in names:
            print(f'Profiling {filename} with {engine}', file=sys.stderr, flush=True)
            runs = []
            for iteration in range(request['warmup'] + request['iterations']):
                # Each query has fresh engine/transport state and its own shared byte budget.
                with MiniS3({filename:path}, settings) as server:
                    operation, close, version = adapter(engine, server, filename, request['threads'])
                    try:
                        start = server.reset_clock()
                        rows = operation(request['query'])
                        elapsed = (time.perf_counter_ns() - start) / 1e6
                        digest = fingerprint(rows)
                        if reference is None:
                            reference = digest
                        elif reference != digest:
                            raise ValueError(f'{engine} returned different query results for {filename}')
                    finally:
                        close()
                    if not server.wait_idle():
                        raise TimeoutError('HTTP request did not finish after the engine closed')
                    events = sorted(server.events, key=lambda e:e['time'])
                    if not events or any(e.get('error') or e['status'] not in (200,206) for e in events):
                        raise ValueError(f'Invalid HTTP trace for {engine}: {events}')
                    for i, event in enumerate(events):
                        event['id'] = i
                    if iteration >= request['warmup']:
                        runs.append(dict(iteration=iteration-request['warmup']+1, elapsed_ms=elapsed, requests=events))
            run = sorted(runs, key=lambda r:r['elapsed_ms'])[len(runs)//2]
            item['engines'].append(dict(name=engine, version=version, requests=run['requests'],
                elapsed_ms=run['elapsed_ms'], samples_ms=[r['elapsed_ms'] for r in runs],
                runs=runs, result_rows=len(rows)))
            print(f'{filename}: {engine}, {len(run["requests"])} requests, {run["elapsed_ms"]:.2f} ms', file=sys.stderr, flush=True)
        result['files'].append(item)
    return result


def write_report(result, path):
    path = Path(path)
    template = Path(__file__).with_name('request_report.html').read_text()
    if template.count('__PLANK_REQUEST_DATA__') != 1:
        raise ValueError('Request viewer template is invalid')
    payload = json.dumps(result, separators=(',',':')).replace('<', '\\u003c')
    html = template.replace('__PLANK_REQUEST_DATA__', payload)
    temp = path.with_name(path.name + f'.{os.getpid()}.tmp')
    try:
        with temp.open('x') as output:
            output.write(html)
        os.link(temp, path)
    finally:
        temp.unlink(missing_ok=True)
