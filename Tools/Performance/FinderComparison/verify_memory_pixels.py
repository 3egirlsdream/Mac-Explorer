#!/usr/bin/env python3
"""Verify all sixty actual preview openings in each complete memory recording."""
import json,subprocess
from control import OUT
all_events=[]
for line in (OUT/'data/control-events.jsonl').open():
 try:e=json.loads(line)
 except json.JSONDecodeError:continue
 if e['op']=='input' and 'inputStartedHostSeconds' in e['result']:all_events.append(e)
summary=[]
for app in ['macexplorer','finder']:
 folder=OUT/'takes'/('memory-10-rounds-'+app)
 clock=json.loads((folder/'source.mov.done.json').read_text())
 actions=[e for e in all_events if e['app']==app and clock['firstPts']<=e['result']['inputStartedHostSeconds']<=clock['lastPts']]
 selected=[]
 for i,e in enumerate(actions):
  args=e['arguments']
  if args.get('action')!='key' or args.get('code')!=49:continue
  after=next(x for x in actions[i+1:] if x['arguments'].get('action')=='key' and x['arguments'].get('code')==53)
  j=len(selected)%6
  selected.append({'app':app,'round':len(selected)//6+1,'kind':'image' if j<5 else 'pdf','file':f'photo-{j+1:03d}.jpg' if j<5 else 'document-01.pdf','inputHostSeconds':e['result']['inputStartedHostSeconds'],'endHoldHostSeconds':after['result']['inputStartedHostSeconds']})
 assert len(selected)==60,(app,len(selected))
 event_path=folder/'workload-preview-events.json';event_path.write_text(json.dumps(selected,ensure_ascii=False,indent=2))
 cache=folder/'workload-preview-pixels.json'
 if not cache.exists():
  with cache.open('w') as f:subprocess.run([str(OUT/'tools/preview-pixels'),str(folder/'source.mov'),str(event_path),str(clock['firstPts']),'--full-decode'],check=True,stdout=f)
 found=json.loads(cache.read_text());missing=[x for i,x in enumerate(selected) if i not in {r['eventIndex'] for r in found}]
 row={'app':app,'expectedPreviews':60,'matchedPreviews':len(found),'missing':missing,'fullSourceDecode':True,'recordedSeconds':clock['lastPts']-clock['firstPts'],'droppedFrames':clock['dropped']};summary.append(row)
 print(app,len(found),'/60 actual previews; full original decode completed',flush=True)
(OUT/'data/memory-workload-pixel-verification.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2))
