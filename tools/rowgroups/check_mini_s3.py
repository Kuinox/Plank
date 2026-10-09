"""Direct deterministic budget and HTTP correctness checks."""
import sys,math,tempfile,urllib.request,urllib.error,time,concurrent.futures
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'Plank.Tool'/'Querying'))
from mini_s3 import Settings,ByteBudget,MiniS3
import threading
# Integrate a ramp at its beginning, across its boundary and after its plateau.
s=Settings(0,1,1000,3)
assert math.isclose(s.duration(1048576,0),(-1+math.sqrt(5))/2)
assert math.isclose(s.duration(2097152,0),1)
assert math.isclose(s.duration(5242880,0),2)
assert math.isclose(s.duration(3145728,2),1)
assert math.isclose(Settings(0,1,0,3).duration(3145728,0),1)
# All concurrent consumers reserve the SAME capacity; idle time earns no credit.
budget=ByteBudget(Settings(0,1,0,1),threading.Event())
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
 due=list(pool.map(lambda _:budget.reserve(1048576,10),range(4)))
assert sorted(due)==[11,12,13,14]
assert budget.reserve(1048576,20)==21
for settings in [(0,0,0,1),(-1,1,0,1),(0,2,0,1),(0,1,-1,2),(math.nan,1,0,1)]:
 try:Settings(*settings);raise AssertionError('Invalid configuration accepted')
 except ValueError:pass
with tempfile.TemporaryDirectory() as tmp:
 path=Path(tmp)/'data.parquet';payload=bytes(range(256))*1024;path.write_bytes(payload)
 with MiniS3({'data.parquet':path},Settings(20,100,0,100)) as server:
  for value,expected in [('bytes=4-99',payload[4:100]),('bytes=-8',payload[-8:]),('bytes=260000-',payload[260000:]),(None,payload)]:
   headers={'Range':value}if value else {}
   start=time.perf_counter()
   with urllib.request.urlopen(urllib.request.Request(server.endpoint+'/plank/data.parquet',headers=headers))as response:
    assert response.read()==expected
    assert response.status==(206 if value else 200)
   assert time.perf_counter()-start>=.019
  with urllib.request.urlopen(urllib.request.Request(server.endpoint+'/plank/data.parquet',method='HEAD'))as response:
   assert response.read()==b'' and int(response.headers['Content-Length'])==len(payload)
  for value in ['bytes=999999-','bytes=20-10','bytes=-0','bytes=0-1,3-4']:
   try:urllib.request.urlopen(urllib.request.Request(server.endpoint+'/plank/data.parquet',headers={'Range':value}));raise AssertionError('Invalid range accepted')
   except urllib.error.HTTPError as e:assert e.code==416
  assert server.wait_idle()
  assert all(e['duration']>=0 for e in server.events)
 with MiniS3({'data.parquet':path},Settings(0,1,0,1)) as server:
     began=time.perf_counter()
     def fetch(_):
         with urllib.request.urlopen(server.endpoint+'/plank/data.parquet') as response:
             return response.read()
     with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
         assert list(pool.map(fetch,range(2)))==[payload,payload]
     assert time.perf_counter()-began >= .49, 'Parallel reads exceeded the shared 1 MiB/s cap'
print('PASS: ramp integral, zero ramp, shared capacity, no idle burst, validation, real HTTP latency, exact full/open/suffix ranges and HEAD')
