#!/usr/bin/env python3
import json
from benchmark import *

def run(app,n,r):
 navigate(app,DATA,'个项目' if app=='finder' else ' 项')
 prepare_path(app,DATA/f'list-{n}')
 take=Recording(app,f'interactive-cached-{n}-{r:02d}-{app}')
 try:
  observation=call('watch-interactive',app,title=f'list-{n}',expected='已选 1 项' if app=='macexplorer' else '选择了1项',x=600.0,y=264.0 if app=='macexplorer' else 165.0)
  result={'app':app,'count':n,'round':r,'definition':'native Return to first confirmed selection of first row in the target folder; selection probed every 25ms plus AX query cost','observation':observation,'seconds':observation['samples'][-1]['hostSeconds']-observation['inputHostSeconds'],'valid':observation['matched'],'recording':str(take.folder.relative_to(OUT))}
  (take.folder/'interactive-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2))
  with (OUT/'data/interactive-cached-trials.jsonl').open('a') as f:f.write(json.dumps(result,ensure_ascii=False)+'\n')
  print(app,n,r,result['seconds'],result['valid'],flush=True)
 finally:take.stop()
 if not result['valid']:raise RuntimeError('Native interaction endpoint failed')
if __name__=='__main__':
 for r in range(1,6):
  for n in [1000,10000,100000]:
   for app in (['macexplorer','finder'] if r%2 else ['finder','macexplorer']):
    folder=OUT/'takes'/f'interactive-cached-{n}-{r:02d}-{app}'
    if (folder/'interactive-result.json').exists():continue
    if folder.exists():folder.rename(folder.with_name(folder.name+'-rejected-input'))
    run(app,n,r)
