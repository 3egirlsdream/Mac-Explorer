#!/usr/bin/env python3
from pathlib import Path
import json,os,plistlib,secrets,shutil,sqlite3,subprocess,time,signal
OUT=Path(__file__).resolve().parents[3]/'outputs/finder-comparison-20261025'
old=json.loads((OUT/'session.json').read_text())
new=Path(os.environ['MACEXPLORER_TEST_ROOT']).resolve()
assert str(new).startswith('/private/tmp/fkfinder-test.') and str(new)!=old['root']
# Stop only the disposable process whose executable and PID are already recorded.
command=subprocess.check_output(['ps','-p',str(old['pid']),'-o','comm='],text=True).strip()
assert command==old['executable'],command
os.kill(old['pid'],signal.SIGTERM)
for _ in range(100):
 try:os.kill(old['pid'],0)
 except ProcessLookupError:break
 time.sleep(.1)
else:raise RuntimeError('Isolated process did not stop')
# APFS clones preserve exact bytes and independent real directory entries.
subprocess.run(['/bin/cp','-cR',old['root']+'/Comparison',str(new/'Comparison')],check=True)
shutil.copyfile(Path(old['root'])/'background.jpg',new/'background.jpg')
token=secrets.token_hex(24);(new/'.demo-owner').write_text(token)
settings={'app_language':'zh-CN','theme_mode':'light','sidebar_show_username':'false','localsend_enabled':'false','file_delivery_enabled':'false','copilot.enabled':'false','ai_analysis_enabled':'false','window.primary.normal_width':'1200','window.primary.normal_height':'680','window.primary.position_x':'120','window.primary.position_y':'70'}
db=new/'.macexplorer/index.db';db.parent.mkdir()
with sqlite3.connect(db) as con:
 con.execute('CREATE TABLE app_settings (key TEXT PRIMARY KEY,value TEXT NOT NULL)')
 con.executemany('INSERT INTO app_settings VALUES (?,?)',settings.items())
log=(OUT/'source/isolated-app-fresh.log').open('w')
p=subprocess.Popen([old['executable']],stdout=log,stderr=subprocess.STDOUT)
new_id={**old,'root':str(new),'token':token,'pid':p.pid,'desktopBackground':str(new/'background.jpg')}
(OUT/f'source/session-observer-diagnostic-{old["pid"]}.json').write_text(json.dumps(old,indent=2))
(OUT/'session.json').write_text(json.dumps(new_id,indent=2))
print('Fresh isolated PID',p.pid,'root',new,flush=True)
# AppKit registers asynchronously after Popen. Wait for this exact process to
# expose its controlled window before any focus or keyboard event is attempted.
import sys
sys.path.insert(0,str(Path(__file__).parent))
from control import call
for _ in range(100):
 try:
  if any(w.get('kCGWindowLayer')==0 for w in call('windows')):break
 except RuntimeError:pass
 time.sleep(.2)
else:raise RuntimeError('Fresh isolated window did not become available')
