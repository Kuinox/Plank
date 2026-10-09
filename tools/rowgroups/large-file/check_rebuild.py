"""Small end-to-end check; no large downloads or engine installations."""
import contextlib
import hashlib
import io
import json
import math
import sys
import tempfile
from pathlib import Path

import pyarrow as pa
import pyarrow.parquet as pq

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT.parent))
sys.path.insert(0, str(ROOT.parents[2] / 'Plank.Tool' / 'Querying'))
from stream_repack import OffsetPages
from prepare_source import normalize
from rebuild_variants import restore, TARGETS
from network import write_report


def main():
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        table = pa.table({
            'unsigned': pa.array([2**63 + i for i in range(225007)], type=pa.uint64()),
            'nullable': [None if i % 7 == 0 else i % 31 for i in range(225007)],
            'string': ['a' if i % 2 else 'zz' for i in range(225007)],
        })
        original, aligned = root / 'original.parquet', root / 'aligned.parquet'
        pq.write_table(table, original, row_group_size=731, use_dictionary=True, data_page_version='2.0')
        with contextlib.redirect_stdout(io.StringIO()):
            info = normalize(original, aligned)
            pages = OffsetPages(aligned)
        assert info['all_values_verified'] and pq.read_table(aligned).equals(table)
        for target in TARGETS:
            variant = root / f'rows-{target}.parquet'
            output = pages.write(variant, target)
            assert output['row_group_count'] == math.ceil(table.num_rows / target)
            assert pq.read_table(variant).equals(table), target
            meta = pq.ParquetFile(variant).metadata
            assert all(meta.row_group(i).num_rows == min(target, table.num_rows - i * target)
                       for i in range(meta.num_row_groups))
            with contextlib.redirect_stdout(io.StringIO()):
                copied = OffsetPages(variant)
            # Check every compressed page, including its header, against source.
            with aligned.open('rb') as src, variant.open('rb') as dst:
                for before, after in zip(pages.columns, copied.columns):
                    assert len(before) == len(after)
                    for a, b in zip(before, after):
                        assert a[1] == b[1]
                        src.seek(int(a[0])); dst.seek(int(b[0]))
                        assert src.read(int(a[1])) == dst.read(int(b[1]))
        data = aligned.read_bytes()
        split = len(data) // 2
        parts = []
        for number, chunk in enumerate((data[:split], data[split:])):
            name = f'part-{number}'
            (root / name).write_bytes(chunk)
            parts.append(dict(number=number, file_name=name, bytes=len(chunk),
                              sha256=hashlib.sha256(chunk).hexdigest()))
        manifest = dict(parts=parts, file_bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
        with contextlib.redirect_stdout(io.StringIO()):
            restored = restore(root, root / 'restored.parquet', manifest)
        assert restored.read_bytes() == data
        (root / 'part-0').write_bytes(b'corrupt')
        try:
            restore(root, root / 'bad.parquet', manifest)
            raise AssertionError('Corrupt part accepted')
        except ValueError:
            assert not (root / 'bad.parquet').exists()
        profile = json.loads((ROOT / 'network-profile.json').read_text())
        assert [f['target'] for f in profile['files']] == TARGETS
        engines = [e for f in profile['files'] for e in f['engines']]
        assert len(engines) == 36 and sum(e['result_verified'] for e in engines) == 33
        assert all(e['result_rows'] == 1 for e in engines if e['result_verified'])
        report = root / 'report.html'
        write_report(profile, report)
        html = report.read_text()
        assert '__PLANK_REQUEST_DATA__' not in html
        assert 'Row group count exceeds DataFusion' in html
        assert 'findIndex(e=>!e.error)' in html
        assert "rs.filter(r=>r.method!=='HEAD')" in html
    print('PASS: normalization, nine exact layouts, decoded values, every copied page, part integrity and saved report')


if __name__ == '__main__':
    main()
