#!/usr/bin/env python3
from pathlib import Path
import json,subprocess,sys,time,tempfile,threading
REPO=Path(__file__).resolve().parents[3]
OUT=REPO/'outputs/finder-comparison-20261025'
EVENT_LOCK=threading.Lock()
NATIVE=OUT/'tools/Comparison Recorder.app/Contents/MacOS/CompareNative'
def identity(app='macexplorer'):
 d=json.loads((OUT/'session.json').read_text())
 if app=='finder':
  d['pid']=int(subprocess.check_output(['pgrep','-x','Finder'],text=True).strip())
  d['executable']='/System/Library/CoreServices/Finder.app/Contents/MacOS/Finder'
 return d
def call(op,app='macexplorer',**kwargs):
 d={**identity(app),**kwargs}
 with tempfile.NamedTemporaryFile(mode='w',dir=d['root'],prefix='comparison-command-',suffix='.json',delete=False) as f:
  json.dump(d,f);p=Path(f.name)
 start=time.monotonic()
 try:result=subprocess.run([str(NATIVE),op,str(p)],capture_output=True,text=True)
 finally:p.unlink(missing_ok=True)
 if result.returncode:raise RuntimeError(result.stdout+result.stderr)
 data=json.loads(result.stdout)
 with EVENT_LOCK, (OUT/'data/control-events.jsonl').open('a') as f:f.write(json.dumps({'startMonotonic':start,'endMonotonic':time.monotonic(),'app':app,'op':op,'arguments':kwargs,'result':data},ensure_ascii=False)+'\n')
 return data
if __name__=='__main__':
 op=sys.argv[1];app=sys.argv[2] if len(sys.argv)>2 else 'macexplorer'
 kwargs=json.loads(sys.argv[3]) if len(sys.argv)>3 else {}
 print(json.dumps(call(op,app,**kwargs),ensure_ascii=False,indent=2))
