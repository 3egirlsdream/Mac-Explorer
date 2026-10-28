#!/usr/bin/env python3
"""Summarize valid primary observations without mixing excluded pilot cohorts."""
import json,statistics
from pathlib import Path
OUT=Path(__file__).resolve().parents[3]/'outputs/finder-comparison-20261025'
rows=[json.loads(x) for x in (OUT/'data/directory-primary-trials.jsonl').read_text().splitlines()]
summary=[]
for n in [1000,10000,100000]:
 for app in ['macexplorer','finder']:
  group=[r for r in rows if r['app']==app and r['count']==n and r['valid']]
  repeats=[r for r in group if r['round']>0]
  assert len(repeats)==5 and len(group)==6,(app,n,len(group))
  med=statistics.median(r['fullReadySeconds'] for r in repeats)
  nearest=min(repeats,key=lambda r:abs(r['fullReadySeconds']-med))
  summary.append(dict(app=app,count=n,repeats=5,fullCountMedianSeconds=med,minSeconds=min(r['fullReadySeconds'] for r in repeats),maxSeconds=max(r['fullReadySeconds'] for r in repeats),firstOpenSeconds=next(r['fullReadySeconds'] for r in group if r['round']==0),interactionUpperBoundMedianSeconds=statistics.median(r['interactiveUpperBoundSeconds'] for r in repeats),selectionConfirmedAll=all(r['interactionConfirmed'] for r in group),medianTake=nearest['recording']))
(OUT/'data/directory-primary-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2))
print(json.dumps(summary,ensure_ascii=False,indent=2))
