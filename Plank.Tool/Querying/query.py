import contextlib
import hashlib
import importlib.metadata
import json
import os
import statistics
import sys
import time

PACKAGES = {'duckdb':'duckdb', 'polars':'polars', 'datafusion':'datafusion', 'clickhouse':'chdb', 'spark':'pyspark'}


def adapter(engine, path, threads):
    version = importlib.metadata.version(PACKAGES[engine])
    if engine == 'duckdb':
        import duckdb
        context = duckdb.connect()
        context.execute(f'SET threads = {threads}')
        context.from_parquet(path).create_view('data')
        return lambda query: context.execute(query).fetchall(), context.close, version
    if engine == 'polars':
        os.environ['POLARS_MAX_THREADS'] = str(threads)
        import polars as pl
        context = pl.SQLContext(data=pl.scan_parquet(path))
        return lambda query: context.execute(query).collect().rows(), lambda: None, version
    if engine == 'datafusion':
        from datafusion import SessionConfig, SessionContext
        context = SessionContext(SessionConfig().with_target_partitions(threads))
        context.register_parquet('data', path)
        def execute(query):
            return [list(row.values()) for batch in context.sql(query).collect() for row in batch.to_pylist()]
        return execute, lambda: None, version
    if engine == 'clickhouse':
        from chdb import session
        context = session.Session()
        context.query(f'SET max_threads = {threads}')
        escaped = path.replace('\\', '\\\\').replace("'", "\\'")
        context.query(f"CREATE VIEW data AS SELECT * FROM file('{escaped}', Parquet)")
        def execute(query):
            return json.loads(str(context.query(query, 'JSONCompact')))['data']
        version = execute('SELECT version()')[0][0]
        return execute, context.close, version
    if engine == 'spark':
        from pyspark.sql import SparkSession
        context = SparkSession.builder.master(f'local[{threads}]').config('spark.ui.enabled','false').config('spark.sql.shuffle.partitions',str(threads)).getOrCreate()
        context.sparkContext.setLogLevel('ERROR')
        context.read.parquet(path).createOrReplaceTempView('data')
        return lambda query: [list(row) for row in context.sql(query).collect()], context.stop, version
    raise ValueError(f'Unknown engine: {engine}')


def fingerprint(rows):
    # Sort row serializations so unordered SQL results still validate deterministically.
    def canonical(value):
        # Parallel reduction order can alter a floating result's last few bits.
        if isinstance(value, float):
            return {'float':format(value, '.12g')}
        if isinstance(value, (list, tuple)):
            return [canonical(v) for v in value]
        if isinstance(value, dict):
            return {k:canonical(v) for k,v in value.items()}
        return value
    serialized = sorted(json.dumps(canonical(list(row)), default=str, sort_keys=True, separators=(',', ':')) for row in rows)
    return hashlib.sha256(json.dumps(serialized, separators=(',', ':')).encode()).digest()


def profile(request):
    results = []
    for engine in request['engines']:
        execute, close, version = adapter(engine, request['file'], request['threads'])
        print(f'Benchmarking query with {engine} {version}', file=sys.stderr, flush=True)
        try:
            expected = execute(request['query'])
            digest, count = fingerprint(expected), len(expected)
            del expected
            for _ in range(request['warmup']):
                if fingerprint(execute(request['query'])) != digest:
                    raise ValueError('Query results vary between executions')
            timings = []
            for _ in range(request['iterations']):
                start = time.perf_counter_ns()
                rows = execute(request['query'])
                timings.append(time.perf_counter_ns() - start)
                if fingerprint(rows) != digest:
                    raise ValueError('Query results vary between measured executions')
            results.append({'engine':engine, 'engine_version':version, 'query':request['query'],
                'time_ns':statistics.mean(timings), 'median_time_ns':statistics.median(timings),
                'iterations':request['iterations'], 'threads':request['threads'], 'result_rows':count})
        finally:
            close()
    return results
