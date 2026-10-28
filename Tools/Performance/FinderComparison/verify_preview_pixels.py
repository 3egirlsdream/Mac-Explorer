#!/usr/bin/env python3
import json,subprocess
from control import OUT
rows=[]
for folder in sorted((OUT/'takes').glob('preview-clean-*')):
 if not (folder/'preview-events.json').exists():continue
 events=json.loads((folder/'preview-events.json').read_text())
 first=json.loads((folder/'source.mov.frames.json').read_text())['firstPts']
 cache=folder/'content-endpoints.json'
 if not cache.exists():
  with cache.open('w') as f:subprocess.run([str(OUT/'tools/preview-pixels'),str(folder/'source.mov'),str(folder/'preview-events.json'),str(first)],check=True,stdout=f)
 found={r['eventIndex']:r for r in json.loads(cache.read_text())}
 for i,e in enumerate(events):
  row={**e,'recording':str(folder.relative_to(OUT)),'valid':i in found,'content':found.get(i)};rows.append(row)
 print(folder.name,len(found),'/',len(events),flush=True)
(OUT/'data/preview-clean-verified.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2))
