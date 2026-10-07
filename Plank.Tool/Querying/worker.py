import contextlib
import importlib.metadata
import json
import os
from pathlib import Path
import random
import statistics
import sys
import time
import uuid

from repack import Pages, footer

PACKAGES = {'duckdb':'duckdb', 'polars':'polars', 'datafusion':'datafusion', 'clickhouse':'chdb', 'spark':'pyspark'}


def footer_adapter(engine, threads):
    version = importlib.metadata.version(PACKAGES[engine])
    if engine == 'duckdb':
        import duckdb
        connection = duckdb.connect()
        connection.execute(f'SET threads = {threads}')
        connection.execute('SET enable_external_file_cache = false')
        return lambda path: connection.execute('SELECT num_rows, num_row_groups FROM parquet_file_metadata(?)', [str(path)]).fetchall(), connection.close, version, 'parquet_file_metadata; native footer read plus SQL/result materialization'
    if engine == 'polars':
        os.environ['POLARS_MAX_THREADS'] = str(threads)
        import polars as pl
        return lambda path: pl.read_parquet_metadata(path), lambda: None, version, 'read_parquet_metadata; native footer read plus custom metadata conversion'
    if engine == 'datafusion':
        from datafusion import SessionConfig, SessionContext
        context = SessionContext(SessionConfig().with_target_partitions(threads))
        return lambda path: context.read_parquet(str(path)).schema(), lambda: None, version, 'read_parquet().schema(); footer-backed schema inference and planning'
    if engine == 'clickhouse':
        from chdb import session
        context = session.Session()
        context.query(f'SET max_threads = {threads}')
        version = json.loads(str(context.query('SELECT version()', 'JSONCompact')))['data'][0][0]
        def inspect(path):
            escaped = str(path).replace('\\', '\\\\').replace("'", "\\'")
            return json.loads(str(context.query(f"SELECT num_rows, num_row_groups FROM file('{escaped}', ParquetMetadata)", 'JSONCompact')))['data']
        return inspect, context.close, version, 'ParquetMetadata; native footer read plus SQL/JSON result materialization'
    if engine == 'spark':
        from pyspark.sql import SparkSession
        session = SparkSession.builder.master(f'local[{threads}]').config('spark.ui.enabled', 'false').getOrCreate()
        session.sparkContext.setLogLevel('ERROR')
        return lambda path: session.read.parquet(str(path)).schema, session.stop, version, 'read.parquet().schema; footer-backed schema inference including JVM task overhead'
    raise ValueError(f'Unknown engine: {engine}')


def details(path, target):
    meta, size, _ = footer(path)
    groups = [g.num_rows for g in meta.row_groups]
    return {'variant_file':str(Path(path).resolve()), 'requested_rows_per_group':target,
            'row_count':meta.num_rows, 'row_group_count':len(groups),
            'min_rows_per_group':min(groups), 'max_rows_per_group':max(groups),
            'footer_size_bytes':size, 'file_size_bytes':Path(path).stat().st_size}


def rowgroups(request):
    import pyarrow as pa
    import pyarrow.parquet as pq
    source = Path(request['file'])
    result = Path(request['output'])
    directory = Path(request['variants'])
    # Exclusive directory creation prevents overwriting any prior run.
    directory.mkdir(parents=True, exist_ok=False)
    marker = directory / '.plank-run-id'
    marker.write_text(request.get('run_id', 'direct-worker'))
    output_temp = result.with_name(result.name + '.' + request.get('run_id', uuid.uuid4().hex) + '.tmp')
    try:
        pages = Pages(source)
        variants = [details(source, None)]
        layouts = {}
        for size in request['sizes']:
            path = directory / f'rows-{size}.parquet'
            cuts = pages.layout(size)
            key = tuple(cuts)
            if key in layouts:
                print(f'Target {size:,} has the same layout as target {layouts[key]:,}; this pair does not measure a size change', file=sys.stderr, flush=True)
            layouts[key] = size
            pages.write(path, cuts)
            info = details(path, size)
            variants.append(info)
            print(f'Repacked target {size:,}: {info["row_group_count"]} groups, actual {info["min_rows_per_group"]:,}–{info["max_rows_per_group"]:,} rows', file=sys.stderr, flush=True)
        results = []
        rng = random.Random(42)
        for engine in request['engines']:
            operation, close, version, description = footer_adapter(engine, request['threads'])
            print(f'Benchmarking {engine} {version}: {description}', file=sys.stderr, flush=True)
            samples = [[] for _ in variants]
            try:
                for info in variants:
                    for _ in range(max(1, request['warmup'])):
                        operation(info['variant_file'])
                for iteration in range(request['iterations']):
                    order = list(range(len(variants)))
                    rng.shuffle(order)
                    for i in order:
                        start = time.perf_counter_ns()
                        value = operation(variants[i]['variant_file'])
                        elapsed = time.perf_counter_ns() - start
                        if engine in ('duckdb', 'clickhouse'):
                            if value[0] != [variants[i]['row_count'], variants[i]['row_group_count']] and tuple(value[0]) != (variants[i]['row_count'], variants[i]['row_group_count']):
                                raise ValueError(f'{engine} returned inconsistent row counts')
                        samples[i].append(elapsed)
                        del value
                for info, timings in zip(variants, samples):
                    results.append({**info, 'engine':engine, 'engine_version':version, 'operation':description,
                        'mean_time_ns':statistics.mean(timings), 'median_time_ns':statistics.median(timings),
                        'min_time_ns':min(timings), 'max_time_ns':max(timings),
                        'iterations':len(timings), 'threads':request['threads']})
            finally:
                close()
        schema = pa.schema([
            ('variant_file', pa.string()), ('requested_rows_per_group', pa.int64()),
            ('row_count', pa.int64()), ('row_group_count', pa.int64()),
            ('min_rows_per_group', pa.int64()), ('max_rows_per_group', pa.int64()),
            ('footer_size_bytes', pa.int64()), ('file_size_bytes', pa.int64()),
            ('engine', pa.string()), ('engine_version', pa.string()), ('operation', pa.string()),
            ('mean_time_ns', pa.float64()), ('median_time_ns', pa.float64()),
            ('min_time_ns', pa.int64()), ('max_time_ns', pa.int64()),
            ('iterations', pa.int64()), ('threads', pa.int64())], metadata={
            b'plank.profile.kind':b'rowgroups-footer', b'plank.profile.version':b'1',
            b'plank.profile.timing':b'Engine-native footer inspection; operation overhead included. Imports, startup, repacking and warmups excluded. Warm OS cache. DuckDB external file cache disabled.',
            b'plank.profile.repacking':b'Compressed page bytes preserved; cuts at shared boundaries. Incompatible dictionaries/codecs retain boundaries. Chunk statistics, page indexes, bloom filters and sort metadata omitted.'})
        pq.write_table(pa.Table.from_pylist(results, schema=schema), output_temp)
        # Atomic publish without replacing any pre-existing destination.
        os.link(output_temp, result)
        output_temp.unlink()
        marker.unlink()
    except BaseException:
        output_temp.unlink(missing_ok=True)
        import shutil
        shutil.rmtree(directory)
        raise


def main():
    request = json.loads(Path(sys.argv[1]).read_text())
    with contextlib.redirect_stdout(sys.stderr):
        if request['kind'] == 'rowgroups':
            rowgroups(request)
            result = str(Path(request['output']).resolve())
        elif request['kind'] == 'query':
            from query import profile
            result = json.dumps(profile(request))
        else:
            raise ValueError('Unsupported worker operation')
    sys.stdout.write(result + '\n')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'Worker failed: {error}', file=sys.stderr)
        sys.exit(1)
