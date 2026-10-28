import json,unittest,subprocess,tempfile
from pathlib import Path
class ComparisonContractTests(unittest.TestCase):
 def test_official_bytes_and_fixture_contract(self):
  root=Path(__file__).resolve().parents[3]/'outputs/finder-comparison-20261025'
  d=json.loads((root/'data/fixture-manifest.json').read_text())
  self.assertEqual([1000,10000,100000],[x['count'] for x in d['directories']])
  images=[x for x in d['files'] if x['path'].endswith('.jpg')]
  self.assertEqual(200,len(images));self.assertEqual(200,len({x['sha256'] for x in images}))
  self.assertEqual(10,len([x for x in d['files'] if x['path'].endswith('.pdf')]))
 def test_native_target_guard_and_window_whitelist(self):
  from control import identity,NATIVE
  for change,message in [({'token':'x'*40},'Invalid isolated profile'),({'executable':'/invalid/comparison-target'},'Unexpected comparison target')]:
   d={**identity(),**change}
   with tempfile.NamedTemporaryFile(mode='w',suffix='.json') as f:
    json.dump(d,f);f.flush()
    result=subprocess.run([str(NATIVE),'footprint',f.name],capture_output=True,text=True)
   self.assertNotEqual(0,result.returncode)
   self.assertEqual(message,json.loads(result.stdout)['error'])
if __name__=='__main__':unittest.main()
