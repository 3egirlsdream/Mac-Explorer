#!/usr/bin/env python3
"""Read original captured frames offline; attach endpoints to an auditable cohort."""
import json,re,subprocess
from pathlib import Path
from control import OUT
def normalized(s):return re.sub(r'[\s,，]','',s)
def count_present(text,n):
 s=normalized(''.join(text))
 return str(n)+'项' in s or str(n)+'个项目' in s
def file_present(text):return '000000' in ''.join(text)
rows=[json.loads(x) for x in (OUT/'data/directory-pixel-trials.jsonl').read_text().splitlines()]
assert len(rows)==36
verified=[]
for row in rows:
 folder=OUT/row['recording'];cache=folder/'pixel-ocr.json'
 if not cache.exists():
  with cache.open('w') as f:subprocess.run([str(OUT/'tools/pixel-endpoints'),str(folder/'source.mov'),row['app'],str(row['count'])],check=True,stdout=f)
 pixels=json.loads(cache.read_text())
 first=json.loads((folder/'source.mov.frames.json').read_text())['firstPts']
 event=row['inputHostSeconds']-first
 count=next((p['pts'] for p in pixels if p['pts']>=event and count_present(p['texts'].get('footer',[]),row['count'])),None)
 content=next((p['pts'] for p in pixels if p['pts']>=event and file_present(p['texts'].get('firstRow',[]))),None)
 row.update(fullReadySeconds=count-event if count is not None else None,firstContentSeconds=content-event if content is not None else None,valid=count is not None and content is not None,status='pixel verified' if count is not None and content is not None else 'requires manual pixel review',captureEventSeconds=event,pixelEvidence=str(cache.relative_to(OUT)))
 if row['app']=='macexplorer' and row['round']==0 and row['count']==1000:
  row['firstOpenDisturbance']='Brief offline OCR pilot overlapped this first-open take; exclude from first-open comparison.'
 verified.append(row)
 print(row['app'],row['count'],row['round'],row['fullReadySeconds'],row['firstContentSeconds'],row['valid'],flush=True)
(OUT/'data/directory-pixel-verified.json').write_text(json.dumps(verified,ensure_ascii=False,indent=2))
