#!/usr/bin/env python3
"""External native-input directory trials with raw capture and AX endpoints."""
from pathlib import Path
import json,subprocess,time,sys
from control import call,identity,OUT,NATIVE
DATA=Path(identity()['root'])/'Comparison'

def wait_for(predicate,timeout=30):
 end=time.monotonic()+timeout
 while time.monotonic()<end:
  value=predicate()
  if value:return value
  time.sleep(.03)
 raise TimeoutError('Observed state did not arrive')

def key(app,code,modifiers=''):return call('input',app,action='key',code=code,modifiers=modifiers)
def activate(app):
 call('input',app,action='activate')
 wait_for(lambda:call('input',app,action='inspect')['frontmostPid']==identity(app)['pid'],5)
 if app=='macexplorer':
  call('raise',app);call('input',app,action='click',x=150,y=140)
 else:
  wait_for(lambda:not any(w['kCGWindowLayer']==3 and w['kCGWindowBounds']['Width']>300 for w in call('windows',app)),5)
  call('input',app,action='click',x=700,y=94)
def prepare_path(app,path,batch=False):
 activate(app)
 if app=='macexplorer':key(app,37,'cmd')
 else:key(app,5,'cmdshift')
 key(app,0,'cmd')
 call('input',app,action='text-batch' if batch else 'text',text=str(path))
def navigate(app,path,expected):
 prepare_path(app,path)
 key(app,36)
 wait_for(lambda:any(w.get('kCGWindowName')==Path(path).name for w in call('windows',app)),20)
 time.sleep(.2)
 return {'titleVerified':True,'contentVerification':'recorded pixels'}

class Recording:
 def __init__(self,app,name,all_target_windows=False,dynamic_owned_windows=False):
  self.folder=OUT/'takes'/name;self.folder.mkdir(parents=True,exist_ok=False)
  d=identity(app);d['path']=str(self.folder/'source.mov')
  if dynamic_owned_windows:
   d['dynamicOwnedWindows']=True
  if app=='finder' and name.startswith('memory-'):
   d['dynamicOwnedWindows']=True;d['dynamicControlledFinderTabs']=True
  windows=call('windows',app)
  d['windowIds']=[w['kCGWindowNumber'] for w in windows if w['kCGWindowLayer']==0]
  if app=='finder':
   titles={'Comparison','list-1000','list-10000','list-100000','text-1000','text-10000','text-100000','images-12MP','pdf-20-pages','finder-organize','finder-delivery','macexplorer-organize','macexplorer-delivery','search'}
   mains=[w for w in windows if w['kCGWindowLayer']==0 and w.get('kCGWindowName') in titles and w['kCGWindowBounds']=={'X':120,'Y':70,'Width':1200,'Height':680}]
   assert len(mains)==1,'Controlled Finder tab group is missing or ambiguous'
   main=mains[0];d['finderWindowId']=main['kCGWindowNumber']
   d['windowIds']=[main['kCGWindowNumber']]+[w['kCGWindowNumber'] for w in windows if all_target_windows and w['kCGWindowLayer']==3 and w['kCGWindowBounds']['Width']>300]
   if all_target_windows:d['rect']=[0,40,1440,810]
  self.path=Path(d['path']);self.config=self.folder/'capture.json';self.config.write_text(json.dumps(d))
  self.log=(self.folder/'capture.log').open('w')
  self.process=subprocess.Popen([str(NATIVE),'record',str(self.config)],stdout=self.log,stderr=subprocess.STDOUT)
  wait_for(lambda:Path(str(self.path)+'.frames.json').exists(),10)
  time.sleep(.4)
 def stop(self):
  time.sleep(.7);Path(str(self.path)+'.stop').touch()
  result=self.process.wait(timeout=15);self.log.close()
  if result:raise RuntimeError(self.config.read_text())
  return json.loads(Path(str(self.path)+'.frames.json').read_text())

def directory_trial(app,n,round_no):
 # Return to the shared parent before every timed navigation.
 navigate(app,DATA,'个项目' if app=='finder' else ' 项')
 prepare_path(app,DATA/f'list-{n}')
 take=Recording(app,f'directory-pixel-{n}-{round_no:02d}-{app}')
 try:
  observed=key(app,36)
  time.sleep(3)
  # A real first-row click and a selected-item status confirm input responsiveness.
  click=call('input',app,action='click',x=600,y=264 if app=='macexplorer' else 165)
  time.sleep(.3)
  result={'app':app,'count':n,'round':round_no,'kind':'first-open' if round_no==0 else 'repeat','fullReadySeconds':None,'fullReadyDefinition':'correct visible status count after native Return; measured offline from original frames','interactionConfirmed':None,'inputHostSeconds':observed['inputStartedHostSeconds'],'clickHostSeconds':click['inputStartedHostSeconds'],'recording':str(take.folder.relative_to(OUT)),'valid':False,'status':'pending pixel verification','footprintAfterTrial':call('footprint',app)}
  (take.folder/'result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2))
  print(json.dumps({k:v for k,v in result.items() if k in ['app','count','round','fullReadySeconds','interactionConfirmed','valid']}),flush=True)
 finally:take.stop()
 with (OUT/'data/directory-pixel-trials.jsonl').open('a') as f:f.write(json.dumps(result,ensure_ascii=False)+'\n')
 return result

def main():
 from generate_filelist_dataset import create_dataset
 for n in [1000,10000,100000]:
  folder=DATA/f'list-{n}'
  if not folder.exists():create_dataset(folder,n,False)
 for r in range(6):
  for n in [1000,10000,100000]:
   for app in (['macexplorer','finder'] if r%2==0 else ['finder','macexplorer']):
    if (OUT/'takes'/f'directory-pixel-{n}-{r:02d}-{app}'/'result.json').exists():continue
    directory_trial(app,n,r)
if __name__=='__main__':
 sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
 main()
