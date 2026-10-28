#!/usr/bin/env python3
"""Complete the valid unpadded Finder rename with a separately recorded copy phase."""
import hashlib,json,time
from benchmark import *
source=DATA/'finder-organize';dest=DATA/'finder-delivery'
expected={f'交付{i}.jpg':hashlib.file_digest((DATA/'images-12MP'/f'photo-{i:03d}.jpg').open('rb'),'sha256').hexdigest() for i in range(1,21)}
actual={p.name:hashlib.file_digest(p.open('rb'),'sha256').hexdigest() for p in source.iterdir()}
assert actual==expected,'The actual Finder index names must map to the same twenty source images'
assert not list(dest.iterdir())
rename=OUT/'takes/organize-index-finder-rename'
clock=json.loads((rename/'source.mov.frames.json').read_text())
events=[]
for line in (OUT/'data/control-events.jsonl').open():
 e=json.loads(line)
 if e['app']=='finder' and e['op']=='input' and clock['firstPts']<=e['result'].get('inputStartedHostSeconds',-1)<=clock['lastPts']:
  events.append(e)
start=next(i for i,e in enumerate(events) if e['arguments'].get('action')=='click' and e['arguments'].get('x')==600 and e['arguments'].get('y')==165)
events=events[start:]
assert len(events)==15,len(events)
prepare_path('finder',source,batch=True);key('finder',36);time.sleep(.7)
take=Recording('finder','organize-index-finder-copy',dynamic_owned_windows=True);activate('finder')
steps=[]
def act(label,**args):
 r=call('input','finder',**args);steps.append({'label':label,'input':args,'native':r});time.sleep(.3)
try:
 act('聚焦已重命名文件列表',action='click',x=600,y=165)
 act('全选20张',action='key',code=0,modifiers='cmd');time.sleep(1)
 selected=call('state','finder')['texts']
 assert any('选择了20项' in s or '已选择20个' in s for s in selected),selected
 act('复制20张',action='key',code=8,modifiers='cmd')
 act('前往交付目录',action='key',code=5,modifiers='cmdshift');act('全选路径',action='key',code=0,modifiers='cmd')
 act('连续输入交付路径',action='text-batch',text=str(dest));act('进入交付目录',action='key',code=36)
 wait_for(lambda:any(w.get('kCGWindowName')==dest.name for w in call('windows','finder')),10);time.sleep(.4)
 act('粘贴20张',action='key',code=9,modifiers='cmd')
 wait_for(lambda:len(list(dest.iterdir()))==20,20);time.sleep(2)
 after={p.name:hashlib.file_digest(p.open('rb'),'sha256').hexdigest() for p in dest.iterdir()};assert after==expected
 result={'app':'finder','valid':True,'targetRule':'交付1.jpg through 交付20.jpg; unpadded prefix+index','renameInputs':events,'copyInputs':steps,'stepCount':len(events)+len(steps),'windowOrSheetTransitions':4,'transitionDefinition':'entering and returning from a separate window or system sheet count once each; same-window text fields excluded','stepDefinition':'each actual click, key/shortcut and continuous text entry counts once; setup and automated waits excluded; observed path, not theoretical minimum','afterSha256':after,'recordings':['takes/organize-index-finder-rename','takes/organize-index-finder-copy'],'editNote':'Rename and copy are recorded as separate phases. No elapsed-time ranking is made. Earlier zero-padding expectation rejected the already valid unpadded rename; this common unpadded rule is also executed in Mac Explorer.'}
 (take.folder/'organize-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2));print('Finder',result['stepCount'],'actual inputs; all20 hashes match',flush=True)
finally:take.stop()
