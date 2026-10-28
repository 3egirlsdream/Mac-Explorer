#!/usr/bin/env python3
"""Read-only QA contact sheets from captured source videos; no editorial rendering."""
import json,subprocess
from pathlib import Path
from PIL import Image,ImageDraw,ImageFont
OUT=Path(__file__).resolve().parents[3]/'outputs/finder-comparison-20261025'
QA=OUT/'qa/clean';QA.mkdir(exist_ok=True)
font=ImageFont.truetype('/System/Library/Fonts/Helvetica.ttc',18)
for app in ['macexplorer','finder']:
 folder=OUT/'takes'/f'preview-clean-01-{app}'
 events=json.loads((folder/'preview-events.json').read_text())
 first=json.loads((folder/'source.mov.frames.json').read_text())['firstPts']
 frames=[]
 for e in events:
  t=e['inputHostSeconds']-first+(1.0 if e['kind']=='image' else 1.7)
  dest=QA/f'{app}-{e["file"]}.jpg'
  subprocess.run(['ffmpeg','-y','-ss',str(t),'-i',str(folder/'source.mov'),'-frames:v','1','-vf','scale=768:432',str(dest)],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
  frames.append((dest,f'{app} / {e["file"]} / {t:.2f}s'))
 sheet=Image.new('RGB',(1536,4*466),'#f4f5f8');draw=ImageDraw.Draw(sheet)
 for i,(p,label) in enumerate(frames):
  x=i%2*768;y=i//2*466;sheet.paste(Image.open(p),(x,y));draw.text((x+10,y+438),label,fill='#202530',font=font)
 sheet.save(QA/f'previews-{app}.jpg',quality=92)
