#!/usr/bin/env python3
"""Sequential main-process physical-footprint observation; no forced GC."""
import json,threading,time,subprocess,sys
from pathlib import Path
if __name__=='__main__' and '--reuse-fresh' not in sys.argv:
 out=Path(__file__).resolve().parents[3]/'outputs/finder-comparison-20261025'
 with (out/'source/memory-fresh-process.log').open('w') as log:
  subprocess.run(['bash','Tools/Testing/run-isolated.sh','python3','Tools/Performance/FinderComparison/restart_isolated.py'],check=True,stdout=log,stderr=subprocess.STDOUT)
from benchmark import *

def go(app,path,expected=None):
 # Fixed reading dwell is not a completion assertion. Actual visible results
 # are checked in the recorded frames after the run; no AX polling here.
 prepare_path(app,path,batch=True);key(app,36)
 wait_for(lambda:any(w.get('kCGWindowName')==Path(path).name for w in call('windows',app)),20)
 time.sleep(2.0 if '100000' in str(path) else 1.0)

def preview_selected(app,count,hold=1.2):
 for i in range(count):
  if i:key(app,125)
  key(app,49);time.sleep(hold);key(app,53);time.sleep(.15)

def run(app):
 activate(app);key(app,53)
 # Equal preconditioning before the 60-second idle baseline.
 for n in [1000,10000,100000]:go(app,DATA/f'list-{n}')
 go(app,DATA/'images-12MP')
 call('input',app,action='click',x=600,y=264 if app=='macexplorer' else 165)
 preview_selected(app,5)
 go(app,DATA/'pdf-20-pages')
 call('input',app,action='click',x=600,y=264 if app=='macexplorer' else 165)
 preview_selected(app,1,2)
 go(app,DATA)
 if app=='finder':
  go(app,DATA/'images-12MP')
  call('input',app,action='click',x=600,y=165);key(app,49);time.sleep(1)
 take=Recording(app,'memory-10-rounds-'+app,all_target_windows=True)
 if app=='finder':key(app,53);go(app,DATA)
 started=time.monotonic();stop=threading.Event();phase={'name':'baseline-idle','round':0};samples=[];rounds=[];errors=[]
 def sample():
  i=0
  while not stop.is_set():
   delay=started+10*i-time.monotonic()
   if delay>0 and stop.wait(delay):break
   try:
    value=call('footprint',app)
    row={**value,'elapsedSeconds':time.monotonic()-started,'phase':phase['name'],'round':phase['round'],'metric':'main-process physical footprint'}
    samples.append(row)
    with (OUT/'data'/f'memory-{app}.jsonl').open('a') as f:f.write(json.dumps(row)+'\n')
   except Exception as ex:errors.append(str(ex));stop.set();break
   i+=1
 thread=threading.Thread(target=sample,daemon=True);thread.start()
 def wait_until(t):
  while time.monotonic()<t:
   if errors:raise RuntimeError(errors)
   time.sleep(min(.25,max(0,t-time.monotonic())))
 try:
  wait_until(started+60)
  for r in range(10):
   wait_until(started+60+r*60);phase.update(name='round-actions',round=r+1)
   begin=time.monotonic()
   for directory,expected in [('list-1000','1000'),('list-10000','10000'),('list-100000','100000'),('pdf-20-pages','10'),('images-12MP','200')]:
    key(app,17,'cmd');time.sleep(.6)
    go(app,DATA/directory,expected+' 项' if app=='macexplorer' else f'{int(expected):,}个项目')
   # All five extra tabs are now open. Preview the same five JPEGs and one PDF.
   opened=call('windows',app)
   (take.folder/f'round-{r+1:02d}-tabs.json').write_text(json.dumps(opened,ensure_ascii=False))
   call('input',app,action='click',x=600,y=264 if app=='macexplorer' else 193)
   preview_selected(app,5)
   go(app,DATA/'pdf-20-pages','10 项' if app=='macexplorer' else '10个项目')
   call('input',app,action='click',x=600,y=264 if app=='macexplorer' else 193)
   preview_selected(app,1,2)
   time.sleep(.4)
   for _ in range(5):
    key(app,13,'cmd');time.sleep(.8)
   ended=time.monotonic()
   windows=call('windows',app)
   title=next(w['kCGWindowName'] for w in windows if w['kCGWindowBounds']['Width']>600 and w['kCGWindowName'])
   row={'round':r+1,'startSeconds':begin-started,'actionEndSeconds':ended-started,'actionDurationSeconds':ended-begin,'returnedTitle':title,'withinOneMinute':ended-begin<60}
   rounds.append(row);print(app,row,flush=True)
   if title!='Comparison' or not row['withinOneMinute']:raise RuntimeError('Round did not restore the baseline tab on schedule')
   phase.update(name='between-round-idle')
  wait_until(started+660);phase.update(name='final-idle')
  wait_until(started+780.5)
 finally:
  stop.set();thread.join(timeout=5);take.stop()
  result={'app':app,'baselineIdleSeconds':60,'roundIntervalSeconds':60,'intendedRounds':10,'rounds':rounds,'finalIdleSeconds':120,'sampleIntervalSeconds':10,'samples':len(samples),'metric':'main-process physical footprint','preconditioning':'same three directories, five JPEGs and one PDF before idle; no AX tree polling during memory workload','valid':len(rounds)==10 and not errors,'errors':errors,'elapsedSeconds':time.monotonic()-started,'recording':str(take.folder.relative_to(OUT))}
  (take.folder/'memory-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2))
 return result
if __name__=='__main__':
 for app in ['macexplorer','finder']:run(app)
