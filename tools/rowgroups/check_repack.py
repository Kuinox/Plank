"""Direct correctness checks; no test runner or telemetry."""
import sys
from pathlib import Path
import tempfile

import pyarrow as pa
import pyarrow.parquet as pq

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'Plank.Tool' / 'Querying'))
from repack import Pages

with tempfile.TemporaryDirectory() as temp:
    root = Path(temp)
    for version in ('1.0', '2.0'):
        for dictionary in (False, True):
            # Different dictionaries per original group exercise forced boundaries.
            table = pa.table({'i':list(range(8192)), 'v':[None if i % 7 == 0 else i % 31 + i // 2048 * 100 for i in range(8192)]})
            source = root / 'source.parquet'
            pq.write_table(table, source, row_group_size=2048, data_page_size=1024,
                write_batch_size=128, use_dictionary=dictionary, compression='zstd', data_page_version=version,
                write_page_index=True, write_page_checksum=True)
            pages = Pages(source)
            for size in (128, 512, 2048, 4096, 16000):
                result = root / f'{version}-{dictionary}-{size}.parquet'
                cuts = pages.layout(size)
                pages.write(result, cuts)
                assert pq.read_table(result).equals(table), (version, dictionary, size)
                copied = Pages(result)
                for original, repacked in zip(pages.columns, copied.columns):
                    assert [x[0][2] for x in original] == [x[0][2] for x in repacked]
                assert cuts[-1] == table.num_rows
    nested = root / 'nested.parquet'
    pq.write_table(pa.table({'nested':[[1, 2], None, [3]]}), nested)
    try:
        Pages(nested)
        raise AssertionError('Nested data should be rejected')
    except ValueError:
        pass
    print('PASS: 20 page-preserving variants, V1/V2, plain/dictionary, nulls, checksums, indexes, decoded equality, unchanged page bytes, nested rejection')
