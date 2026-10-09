"""Download/normalize the public dataset and verify every decoded value."""
import argparse
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen

import pyarrow as pa
import pyarrow.parquet as pq

ROOT = Path(__file__).resolve().parent
BATCH_ROWS = 1_000_000


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as src:
        while chunk := src.read(8 * 1024 * 1024):
            h.update(chunk)
    return h.hexdigest()


def aligned_batches(file):
    """Ignore incoming Arrow/Parquet boundaries when forming 1M-row groups."""
    chunks, count = [], 0
    for batch in file.iter_batches(batch_size=65536, use_threads=False):
        start = 0
        while start < batch.num_rows:
            take = min(BATCH_ROWS - count, batch.num_rows - start)
            chunks.append(batch.slice(start, take))
            start += take
            count += take
            if count == BATCH_ROWS:
                yield pa.Table.from_batches(chunks).combine_chunks()
                chunks, count = [], 0
    if chunks:
        yield pa.Table.from_batches(chunks).combine_chunks()


def fingerprint(table):
    table = table.replace_schema_metadata(None).combine_chunks()
    # IPC includes undefined values underneath nulls. Rebuild nullable arrays
    # so logically equal columns cannot differ solely in those unused bytes.
    arrays = [pa.array(column.to_pylist(), type=field.type, from_pandas=False)
              if column.null_count else column.chunk(0)
              for field, column in zip(table.schema, table.columns)]
    table = pa.Table.from_arrays(arrays, schema=table.schema)
    sink = pa.BufferOutputStream()
    with pa.ipc.new_stream(sink, table.schema) as stream:
        stream.write_table(table)
    return hashlib.sha256(sink.getvalue()).hexdigest()


def normalize(source, output):
    file = pq.ParquetFile(source)
    fingerprints = []
    with pq.ParquetWriter(output, file.schema_arrow, compression='zstd', compression_level=3,
                         use_dictionary=False, data_page_version='1.0', write_batch_size=1000,
                         max_rows_per_page=1000, data_page_size=64 * 1024 * 1024) as writer:
        for i, table in enumerate(aligned_batches(file)):
            fingerprints.append(fingerprint(table))
            writer.write_table(table, row_group_size=BATCH_ROWS)
            print(f'Wrote normalized group {i + 1}', flush=True)
    prepared = pq.ParquetFile(output)
    actual = [fingerprint(table) for table in aligned_batches(prepared)]
    if fingerprints != actual or prepared.metadata.num_rows != file.metadata.num_rows:
        raise ValueError('Decoded values changed during normalization')
    return dict(rows=file.metadata.num_rows, columns=len(file.schema_arrow), page_rows=1000,
                compression='ZSTD', compression_level=3, use_dictionary=False,
                all_values_verified=True, fingerprints=fingerprints,
                fingerprint_format='SHA256(Arrow IPC stream, no metadata, combined chunks, canonical null storage)')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    origin = parser.add_mutually_exclusive_group(required=True)
    origin.add_argument('--source', type=Path, help='Existing original ClickBench download')
    origin.add_argument('--download', action='store_true')
    parser.add_argument('--output', type=Path, required=True, help='Working directory')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    known = json.loads((ROOT / 'preparation.json').read_text())
    prepared = args.output / 'hits-aligned.parquet'
    if prepared.exists():
        raise FileExistsError(prepared)
    source = args.source or args.output / 'hits-original.parquet'
    if args.download and not source.exists():
        temporary = source.with_name(source.name + '.incomplete')
        with urlopen(known['source_url'], timeout=120) as src, temporary.open('xb') as dst:
            while chunk := src.read(8 * 1024 * 1024):
                dst.write(chunk)
        if temporary.stat().st_size != known['source_size'] or digest(temporary) != known['source_sha256']:
            raise ValueError('Downloaded source checksum/size mismatch')
        temporary.rename(source)
    if source.stat().st_size != known['source_size'] or digest(source) != known['source_sha256']:
        raise ValueError('Original source checksum/size mismatch')
    temporary = prepared.with_name(prepared.name + '.incomplete')
    if temporary.exists():
        raise FileExistsError(temporary)
    info = normalize(source, temporary)
    info.update(source_url=known['source_url'], source_size=known['source_size'],
                source_sha256=known['source_sha256'], prepared_file=str(prepared.resolve()),
                prepared_size=temporary.stat().st_size, prepared_sha256=digest(temporary))
    temporary.rename(prepared)
    (args.output / 'preparation.json').write_text(json.dumps(info, indent=2) + '\n')
    print(prepared.resolve())


if __name__ == '__main__':
    main()
