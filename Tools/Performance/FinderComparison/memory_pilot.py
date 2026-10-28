#!/usr/bin/env python3
"""Short pixel-verifiable rehearsal; never used as the ten-round memory result."""
import json,time,sys
from memory_trials import go,preview_selected
from benchmark import *
for app in (sys.argv[1:] or ['macexplorer','finder']):
 activate(app);key(app,53)
 go(app,DATA/'images-12MP');call('input',app,action='click',x=600,y=264 if app=='macexplorer' else 165)
 key(app,49);time.sleep(1)
 take=Recording(app,'memory-pilot-'+app,all_target_windows=True)
 key(app,53);go(app,DATA)
 markers=[]
 try:
  for directory in ['list-1000','list-10000','list-100000','pdf-20-pages','images-12MP']:
   key(app,17,'cmd');time.sleep(.6);go(app,DATA/directory)
  markers.append({'kind':'five-extra-tabs','hostSeconds':call('footprint',app)['hostSeconds']})
  call('input',app,action='click',x=600,y=264 if app=='macexplorer' else 193)
  for i in range(5):
   if i:key(app,125)
   key(app,49);time.sleep(.8)
   markers.append({'kind':f'image-{i+1:03d}','hostSeconds':call('footprint',app)['hostSeconds']})
   key(app,53);time.sleep(.15)
  go(app,DATA/'pdf-20-pages');call('input',app,action='click',x=600,y=264 if app=='macexplorer' else 193)
  key(app,49);time.sleep(1.5);markers.append({'kind':'pdf-01','hostSeconds':call('footprint',app)['hostSeconds']});key(app,53)
  for _ in range(5):key(app,13,'cmd');time.sleep(.8)
  markers.append({'kind':'returned-baseline','hostSeconds':call('footprint',app)['hostSeconds']})
 finally:take.stop()
 (take.folder/'markers.json').write_text(json.dumps(markers,ensure_ascii=False,indent=2))
 print(app,[(w.get('kCGWindowName'),w['kCGWindowNumber']) for w in call('windows',app)],flush=True)
