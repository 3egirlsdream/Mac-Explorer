#!/usr/bin/env python3
import json,time
from benchmark import *

def run(app,r):
 navigate(app,DATA,'个项目' if app=='finder' else ' 项')
 navigate(app,DATA/'images-12MP','200 项' if app=='macexplorer' else '200个项目')
 call('input',app,action='click',x=600,y=264 if app=='macexplorer' else 165)
 # Warm only the persistent capture panel, allowing an explicit window whitelist.
 key(app,49);time.sleep(1)
 take=Recording(app,f'preview-clean-{r:02d}-{app}',all_target_windows=True)
 key(app,53);time.sleep(.3)
 events=[]
 try:
  for kind,count,hold in [('image',5,1.2),('pdf',2,2.0)]:
   if kind=='pdf':
    navigate(app,DATA/'pdf-20-pages','10 项' if app=='macexplorer' else '10个项目')
    call('input',app,action='click',x=600,y=264 if app=='macexplorer' else 165)
   for i in range(count):
    if i:key(app,125)
    start=key(app,49)
    time.sleep(hold)
    events.append({'app':app,'round':r,'phase':'initial-observation' if r==0 else 'repeat','kind':kind,'file':f'photo-{i+1:03d}.jpg' if kind=='image' else f'document-{i+1:02d}.pdf','inputHostSeconds':start['inputStartedHostSeconds'],'endHoldHostSeconds':call('footprint',app)['hostSeconds'],'contentEndpoint':'pending pixel verification'})
    key(app,53);time.sleep(.2)
 finally:take.stop()
 (take.folder/'preview-events.json').write_text(json.dumps(events,ensure_ascii=False,indent=2))
 print(app,r,len(events),'events recorded',flush=True)
 with (OUT/'data/preview-clean-events.jsonl').open('a') as f:
  for e in events:f.write(json.dumps({**e,'recording':str(take.folder.relative_to(OUT))},ensure_ascii=False)+'\n')
if __name__=='__main__':
 for r in range(6):
  for app in (['macexplorer','finder'] if r%2==0 else ['finder','macexplorer']):
   folder=OUT/'takes'/f'preview-clean-{r:02d}-{app}'
   if (folder/'preview-events.json').exists():continue
   if folder.exists():folder.rename(folder.with_name(folder.name+'-rejected-interruption'))
   activate(app);key(app,53)
   run(app,r)
