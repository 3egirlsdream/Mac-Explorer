#!/usr/bin/env python3
import hashlib,json,time
from benchmark import *
def run(app,index=False):
 suffix='-index' if index else ''
 source=DATA/f'{app}-organize{suffix}';dest=DATA/f'{app}-delivery{suffix}'
 target=lambda i:f'交付{i}.jpg' if index else f'交付{i:05d}.jpg'
 assert sorted(p.name for p in source.iterdir())==[f'photo-{i:03d}.jpg' for i in range(1,21)]
 assert not list(dest.iterdir())
 before={p.name:hashlib.file_digest(p.open('rb'),'sha256').hexdigest() for p in source.iterdir()}
 prepare_path(app,source,batch=True);key(app,36);time.sleep(1)
 take=Recording(app,('organize-index-' if index else 'organize-final-')+app,dynamic_owned_windows=True)
 activate(app)
 steps=[]
 def action(label,**args):
  result=call('input',app,**args);steps.append({'step':len(steps)+1,'label':label,'input':args,'native':result});time.sleep(.2)
 def press(label,code,modifiers=''):action(label,action='key',code=code,modifiers=modifiers)
 def click(label,x,y,**args):action(label,action='click',x=x,y=y,**args)
 try:
  click('聚焦源文件列表',600,264 if app=='macexplorer' else 165);press('全选20张',0,'cmd')
  if app=='macexplorer':
   press('打开批量重命名',15,'cmdshift');time.sleep(.4)
   click('打开预设',420,124);press('预设第一项',125);press('项目编号预设',125);press('选定预设',36)
   click('编辑名称模板',480,230);press('全选模板',0,'cmd');action('连续输入同一目标名称规则',action='text-batch',text='交付{n:0}' if index else '交付{n:00000}')
   time.sleep(.7);click('应用20项重命名',1240,740)
  else:
   time.sleep(.5)
   selected=call('state',app)['texts']
   assert any('选择了20项' in s or '已选择20个' in s for s in selected),selected
   click('打开所选文件菜单',600,165,right=True);time.sleep(.3);click('重新命名20项',660,318);time.sleep(.4)
   assert '替换文本' in call('state',app)['texts'],'Rename sheet did not open in the original mode'
   click('打开命名模式',538,389);press('添加文本选项',125);press('格式选项',125);press('选定格式',36)
   click('打开编号格式',608,401);press('名称和计数选项',125);press('选定名称和计数',36)
   click('编辑自定义格式',610,431);press('全选格式',0,'cmd');action('连续输入同一名称前缀',action='text-batch',text='交付')
   time.sleep(.7);click('应用20项重命名',906,470)
  wait_for(lambda:set(p.name for p in source.iterdir())=={target(i) for i in range(1,21)},10)
  if app=='macexplorer':
   time.sleep(.5);click('完成并返回源文件列表',1254,740)
   wait_for(lambda:not any(w.get('kCGWindowName','').startswith('批量重命名') for w in call('windows',app)),10)
  time.sleep(.5);click('聚焦重命名后的文件列表',600,264 if app=='macexplorer' else 165)
  press('全选已重命名文件',0,'cmd');press('复制20张',8,'cmd')
  press('输入交付目录',37 if app=='macexplorer' else 5,'cmd' if app=='macexplorer' else 'cmdshift')
  press('全选路径',0,'cmd');action('连续输入目标路径',action='text-batch',text=str(dest));press('进入空交付目录',36)
  wait_for(lambda:any(w.get('kCGWindowName')==dest.name for w in call('windows',app)),10)
  time.sleep(.5);press('粘贴20张',9,'cmd')
  wait_for(lambda:len(list(dest.iterdir()))==20,20);time.sleep(3)
  expected={target(i):before[f'photo-{i:03d}.jpg'] for i in range(1,21)}
  after={p.name:hashlib.file_digest(p.open('rb'),'sha256').hexdigest() for p in dest.iterdir()}
  assert after==expected
  result={'app':app,'valid':True,'steps':steps,'stepCount':len(steps),'windowOrSheetTransitions':2 if app=='macexplorer' else 4,'transitionDefinition':'entering and returning from a separate window or system sheet count once each; same-window text fields excluded','stepDefinition':'each click, key/shortcut and continuous text entry counts once; automated waiting and setup navigation excluded','beforeSha256':before,'afterSha256':after,'recording':str(take.folder.relative_to(OUT))}
  (take.folder/'organize-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2));print(app,len(steps),'steps; 20 hashes match',flush=True)
 finally:take.stop()
if __name__=='__main__':
 for app in ['macexplorer','finder']:run(app)
