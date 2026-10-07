"""Direct CLI integration checks. Requires pyarrow, no test runner telemetry."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

import pyarrow as pa
import pyarrow.parquet as pq

repo = Path(__file__).resolve().parents[2]
dotnet, worker = sys.argv[1:3]
engines = sys.argv[3] if len(sys.argv) > 3 else 'duckdb'
base = [dotnet, str(repo/'Plank.Tool/bin/Release/net10.0/Plank.Tool.dll')]
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1')

def run(*args, success=True):
    process = subprocess.run(base+list(map(str,args)), text=True, capture_output=True, env=env)
    assert (process.returncode == 0) == success, process.stderr
    return process

with tempfile.TemporaryDirectory() as temporary:
    root = Path(temporary)
    source = root/'input with spaces.parquet'
    table = pa.table({'id':pa.array([None if i%7==0 else i%17 for i in range(128)],type=pa.int32()),
        'number':[i/17 for i in range(128)],'name':[None if i%9==0 else str(i%11) for i in range(128)]})
    pq.write_table(table,source,row_group_size=32,compression='zstd')
    assert 'json, tsv, lines' in run('schema').stdout
    schema = json.loads(run('schema','json',source).stdout)
    assert [(r['id'],r['physical_type']) for r in schema] == [(0,'Int32'),(1,'Double'),(2,'ByteArray')]
    assert run('schema','lines',source).stdout.splitlines() == ['id','number','name']
    assert run('schema','tsv',source).stdout.splitlines()[0].startswith('id\tcolumn_path\tphysical_type')
    first, second = root/'first.parquet', root/'second.parquet'
    for output, column in ((first,'0'),(second,'name')):
        done = run('profile','encoding',source,'--columns',column,'--encodings','Plain,PlainDictionary,RleDictionary',
            '--compressions','None,Zstd','--compression-levels','1,3','--iterations','2','--warmup','1','--output',output)
        assert done.stdout.strip() == str(output)
        results = pq.read_table(output)
        assert results.num_rows == 6 # Dictionary aliases produce one encoding, not two.
        assert results.schema.field('compression_level').type == pa.int32()
        assert set(results.column('physical_type').to_pylist()) == {schema[0 if column=='0' else 2]['physical_type']}
        assert set(results.column('encoding').to_pylist()) == {'Plain','RleDictionary'}
        assert all(r['write_time_ns']>0 and r['read_time_ns']>0 for r in results.to_pylist())
        original = sum(pq.read_metadata(source).row_group(g).column(0 if column=='0' else 2).total_compressed_size for g in range(4))
        assert set(results.column('original_size_bytes').to_pylist()) == {original}
    merged = root/'merged.parquet'
    assert run('merge',merged,first,second).stdout.strip() == str(merged)
    assert pq.read_table(merged).equals(pa.concat_tables([pq.read_table(first),pq.read_table(second)]))
    # Exact page payload preservation rather than merely equivalent decoded values.
    def chunks(file):
        meta=pq.read_metadata(file); raw=Path(file).read_bytes(); result=[]
        for g in range(meta.num_row_groups):
            for c in range(meta.num_columns):
                col=meta.row_group(g).column(c); start=col.dictionary_page_offset if col.has_dictionary_page else col.data_page_offset
                result.append(raw[start:start+col.total_compressed_size])
        return result
    assert chunks(merged) == chunks(first)+chunks(second)
    before=merged.read_bytes();run('merge',merged,first,second,success=False);assert merged.read_bytes()==before
    mismatch=root/'mismatch.parquet';run('merge',mismatch,first,source,success=False);assert not mismatch.exists()
    report=root/'report.html';run('report',merged,report,'--title','Restored <report>')
    html=report.read_text();embedded=json.loads(__import__('re').search(r'<script id="profile-data" type="application/json">(.*?)</script>',html,__import__('re').S).group(1))
    assert embedded['name']=='Restored <report>' and len(embedded['rows'])==12
    assert 'feature-level' in html and 'clear-filters' in html and 'physical_type' in html
    query=root/'query.parquet';done=run('profile','query',source,'--query','SELECT SUM(number) AS total FROM data','--engines',engines,
        '--worker-directory',worker,'--iterations','2','--warmup','1','--output',query)
    assert done.stdout.strip()==str(query)
    rows=pq.read_table(query).to_pylist();assert len(rows)==len(engines.split(','))
    assert all(r['result_rows']==1 and r['iterations']==2 and r['time_ns']>0 for r in rows)
    invalid=root/'invalid-query.parquet';run('profile','query',source,'--query','SELECT missing_column FROM data','--engines','duckdb',
        '--worker-directory',worker,'--output',invalid,success=False);assert not invalid.exists()
    run('profile','encoding',source,'--columns','missing',success=False)
    run('report',source,root/'invalid.html',success=False);assert not (root/'invalid.html').exists()
print('PASS: schema formats, column IDs/paths, dictionary alias normalization, levels, original chunk sizes, timed round-trips, page-preserving merge, atomic failures, HTML generation, SQL engines and invalid queries')
