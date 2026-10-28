#!/usr/bin/env python3
"""Fresh, synthetic comparison fixtures. Invoke through run-isolated.sh."""
from pathlib import Path
import hashlib,json,os,secrets,shutil,sqlite3,subprocess,sys,time
from PIL import Image,ImageDraw,ImageFont
import numpy as np
from reportlab.pdfgen import canvas
from pypdf import PdfReader
REPO=Path(__file__).resolve().parents[3]
OUT=REPO/'outputs/finder-comparison-20261025'
root=Path(os.environ['MACEXPLORER_TEST_ROOT']).resolve()
assert str(root).startswith('/private/tmp/fkfinder-test.')
token=secrets.token_hex(24)
(root/'.demo-owner').write_text(token)
data=root/'Comparison'
data.mkdir()
manifest={'schemaVersion':1,'root':str(root),'createdUtc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),'synthetic':True,'files':[]}
def record(p):
 manifest['files'].append({'path':str(p.relative_to(data)),'bytes':p.stat().st_size,'sha256':hashlib.file_digest(p.open('rb'),'sha256').hexdigest()})
sys.path.insert(0,str(REPO/'Tools/Performance'))
from generate_filelist_dataset import create_dataset
for n in [1000,10000,100000]:
 folder=data/f'text-{n}'
 create_dataset(folder,n,False)
 # Manifest stores a deterministic tree digest rather than 111k redundant JSON rows.
 h=hashlib.sha256()
 for p in sorted(folder.iterdir()):h.update(p.name.encode()+b'\0'+p.read_bytes())
 manifest.setdefault('directories',[]).append({'name':folder.name,'count':n,'treeSha256':h.hexdigest(),'first':'item-000000.txt','last':f'item-{n-1:06d}.txt'})
 print('prepared',n,flush=True)
images=data/'images-12MP';images.mkdir()
font=ImageFont.truetype('/System/Library/Fonts/Helvetica.ttc',160)
rng=np.random.default_rng(20261025)
# Same structured, detailed synthetic load family; unique image number, no copied photo.
y,x=np.mgrid[:3000,:4000]
base=np.empty((3000,4000,3),dtype=np.uint8)
base[:,:,0]=((x//16+y//24)%200+28).astype('uint8')
base[:,:,1]=((x//27-y//19)%180+40).astype('uint8')
base[:,:,2]=((x//35+y//31)%170+55).astype('uint8')
for i in range(200):
 a=Image.fromarray(np.roll(base,i*13,axis=1))
 d=ImageDraw.Draw(a);d.rectangle((120,100,2600,450),fill='#102030');d.text((170,160),f'SYNTHETIC 12MP  /  {i+1:03d}',font=font,fill='white')
 p=images/f'photo-{i+1:03d}.jpg';a.save(p,quality=90,subsampling=0);record(p)
 if i%50==0:print('images',i,flush=True)
pdfs=data/'pdf-20-pages';pdfs.mkdir()
for i in range(10):
 p=pdfs/f'document-{i+1:02d}.pdf';c=canvas.Canvas(str(p),pagesize=(612,792),invariant=1,pageCompression=1)
 for page in range(20):
  c.setFillColorRGB(.06,.14,.23);c.rect(0,580,612,212,fill=1,stroke=0)
  c.setFillColorRGB(1,1,1);c.setFont('Helvetica-Bold',26);c.drawString(42,715,'SYNTHETIC DOCUMENT')
  c.setFont('Helvetica',18);c.drawString(42,665,f'Document {i+1:02d}  /  Page {page+1:02d} of 20')
  c.setFillColorRGB(.1,.2,.3);c.setFont('Helvetica',12)
  for line in range(20):c.drawString(42,540-line*21,f'Controlled preview fixture. Row {line+1:02d}. Same bytes in both applications.')
  c.showPage()
 c.save();assert len(PdfReader(str(p)).pages)==20;record(p)
search=data/'search';search.mkdir()
for name in ['unique-needle-731.txt']+[f'twenty-match-{i:02d}.txt' for i in range(20)]+[f'ordinary-{i:03d}.txt' for i in range(200)]:
 p=search/name;p.write_text('Synthetic filename search fixture.\n');record(p)
for app in ['macexplorer','finder']:
 for folder in ['organize','delivery']:
  (data/f'{app}-{folder}').mkdir()
 for i in range(20):shutil.copyfile(images/f'photo-{i+1:03d}.jpg',data/f'{app}-organize'/f'photo-{i+1:03d}.jpg')
shutil.copyfile(REPO/'docs/Assets/alpine-lake.jpg',root/'background.jpg')
settings={'app_language':'zh-CN','theme_mode':'light','sidebar_show_username':'false','localsend_enabled':'false','file_delivery_enabled':'false','copilot.enabled':'false','ai_analysis_enabled':'false','window.primary.normal_width':'1200','window.primary.normal_height':'680','window.primary.position_x':'120','window.primary.position_y':'70'}
db=root/'.macexplorer/index.db';db.parent.mkdir()
with sqlite3.connect(db) as con:
 con.execute('CREATE TABLE app_settings (key TEXT PRIMARY KEY,value TEXT NOT NULL)')
 con.executemany('INSERT INTO app_settings VALUES (?,?)',settings.items())
(OUT/'data').mkdir(exist_ok=True)
(OUT/'data/fixture-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2))
exe=OUT/'source/release/Mac Explorer.app/Contents/MacOS/MacExplorer'
log=(OUT/'source/isolated-app.log').open('w')
p=subprocess.Popen([str(exe)],stdout=log,stderr=subprocess.STDOUT)
identity={'root':str(root),'token':token,'pid':p.pid,'executable':str(exe),'desktopBackground':str(root/'background.jpg'),'rect':[0,0,1440,810]}
(OUT/'session.json').write_text(json.dumps(identity,indent=2))
print('SESSION',identity,flush=True)
