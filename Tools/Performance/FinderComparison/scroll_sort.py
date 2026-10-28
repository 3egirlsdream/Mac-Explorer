#!/usr/bin/env python3
import json,time
from benchmark import *
for app in ['macexplorer','finder']:
 prepare_path(app,DATA/'list-100000',batch=True);key(app,36);time.sleep(2)
 take=Recording(app,'scroll-sort-'+app);markers=[];started=time.monotonic()
 try:
  time.sleep(.5)
  for direction in [-1,1]:
   for _ in range(20):
    call('input',app,action='scroll',x=800,y=450,delta=direction*120);time.sleep(.08)
   markers.append({'kind':'scroll-down' if direction<0 else 'scroll-up','hostSeconds':call('footprint',app)['hostSeconds']});time.sleep(1)
  for i in range(2):
   call('input',app,action='click',x=500,y=222 if app=='macexplorer' else 135);time.sleep(2)
   markers.append({'kind':f'name-sort-toggle-{i+1}','hostSeconds':call('footprint',app)['hostSeconds']})
  call('input',app,action='click',x=600,y=264 if app=='macexplorer' else 165)
  time.sleep(max(1,20-(time.monotonic()-started)))
 finally:take.stop()
 (take.folder/'markers.json').write_text(json.dumps(markers,ensure_ascii=False,indent=2));print(app,'scroll/sort captured',flush=True)
