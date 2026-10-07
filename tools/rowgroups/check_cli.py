"""End-to-end CLI checks against an existing worker; no network needed."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile

import pyarrow as pa
import pyarrow.parquet as pq

repo = Path(__file__).resolve().parents[2]
dotnet, worker = sys.argv[1:]
command = [dotnet, str(repo / 'Plank.Tool/bin/Release/net10.0/Plank.Tool.dll'), 'profile', 'rowgroups']
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1')
with tempfile.TemporaryDirectory() as temporary:
    root = Path(temporary)
    source = root / 'input with spaces.parquet'
    expected = pa.table({'i':range(8192), 'nullable':[None if i % 9 == 0 else i%17 for i in range(8192)]})
    pq.write_table(expected, source, row_group_size=2048, use_dictionary=False, data_page_size=1024, write_batch_size=128)
    result = root / 'result.parquet'
    args = [str(source), '--sizes', '512,4096', '--engines', 'duckdb', '--iterations', '3', '--warmup', '1', '--worker-directory', worker, '--output', str(result)]
    completed = subprocess.run(command + args, env=env, text=True, capture_output=True)
    assert completed.returncode == 0, completed.stderr
    assert completed.stdout.strip() == str(result), completed.stdout
    assert 'Installing Python packages' not in completed.stderr
    records = pq.read_table(result).to_pylist()
    assert len(records) == 3 and records[0]['requested_rows_per_group'] is None
    for record in records:
        assert record['iterations'] == 3 and record['median_time_ns'] > 0
        assert pq.read_table(record['variant_file']).equals(expected)
    original = result.read_bytes()
    completed = subprocess.run(command + args, env=env, text=True, capture_output=True)
    assert completed.returncode != 0 and result.read_bytes() == original
    for extra in ([str(root/'missing.parquet')], [str(source),'--sizes','-1'], [str(source),'--engines','invalid'], [str(source),'--iterations','0']):
        completed = subprocess.run(command + extra, env=env, text=True, capture_output=True)
        assert completed.returncode != 0 and not completed.stdout
    nested = root / 'nested.parquet'
    pq.write_table(pa.table({'list':[[1], None]}), nested)
    completed = subprocess.run(command + [str(nested),'--worker-directory', worker,'--engines','duckdb'], env=env, text=True, capture_output=True)
    assert completed.returncode != 0 and 'non-repeated' in completed.stderr
    assert not (root/'nested.rowgroups-profile').exists()
    assert not (root/'nested.rowgroups-profile.parquet').exists()
print('PASS: cached CLI, paths with spaces, stdout path, results schema/timing, generated values, overwrite/input/engine/size/iteration guards, failed-run cleanup')
