#!/usr/bin/env python3
"""Extend QQ reference textures without changing the scale or proportions of their motifs.

Usage (Python 3.12, Pillow 13.0.0, vtracer 0.6.15):
  python3 Tools/Design/generate-theme-vectors.py /path/to/原始背景纹理
The PNG references are traced to paths, including their transparent tonal layers.
The application only loads the packaged SVGs; the reference folder is a design input.
"""
import argparse
import hashlib
import json
from pathlib import Path
import tempfile
import xml.etree.ElementTree as ET

NS = 'http://www.w3.org/2000/svg'
ET.register_namespace('', NS)
ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'Assets/BackgroundThemes'


def source_vector(file, ink):
    root = ET.parse(file).getroot()
    for e in root.iter():
        if e.tag.endswith('mask'):
            continue
        for key in ('fill', 'stroke'):
            color = e.get(key)
            if color == 'white': e.set(key, ink)
            elif color in ('#1D2830', '#1A1B1E', '#2D3235'):
                e.set(key, {'#1D2830':'#91A5AA','#1A1B1E':'#78929B','#2D3235':'#A5B7AD'}[color])
    # Preserve luminance masks; recoloring them changes the drawing's geometry.
    originals = ET.parse(file).getroot().iter(f'{{{NS}}}mask')
    for dest, original in zip(root.iter(f'{{{NS}}}mask'), originals):
        for a,b in zip(dest.iter(),original.iter()):
            for key in ('fill','stroke'):
                if key in b.attrib: a.set(key,b.get(key))
    return '<g fill="none">'+''.join(ET.tostring(e,encoding='unicode') for e in root)+'</g>'


def trace_rgba(file):
    from PIL import Image, ImageFilter
    import vtracer
    source=Image.open(file).convert('RGBA')
    width,height=source.size
    # Retain the complete composition and original alpha. Smoothing removes the
    # PNG's grain, rather than replacing the illustration with a silhouette.
    image=source.resize((780,round(780*height/width)),Image.Resampling.LANCZOS).filter(ImageFilter.GaussianBlur(.8))
    quant=image.quantize(colors=40,method=Image.Quantize.FASTOCTREE,dither=Image.Dither.NONE)
    palette=quant.getpalette('RGBA')
    colors=[tuple(palette[i:i+4]) for i in range(0,len(palette),4)]
    max_alpha=max(c[3] for c in colors)
    # VTracer does not emit fractional alpha. Encode each RGBA palette entry as
    # a unique opaque key, trace it, then restore its color and alpha per path.
    keys=[((i*67+31)%251,(i*113+47)%251,(i*173+71)%251) for i in range(len(colors))]
    pixels=[]
    for index in quant.get_flattened_data():
        pixels.append((*keys[index],255) if colors[index][3]>=3 else (0,0,0,0))
    encoded=Image.new('RGBA',quant.size);encoded.putdata(pixels)
    with tempfile.TemporaryDirectory() as tmp:
        png,svg=Path(tmp)/'palette.png',Path(tmp)/'traced.svg';encoded.save(png)
        vtracer.convert_image_to_svg_py(str(png),str(svg),colormode='color',hierarchical='cutout',mode='spline',filter_speckle=10,color_precision=8,layer_difference=0,corner_threshold=75,length_threshold=3.5,max_iterations=10,splice_threshold=45,path_precision=2)
        root=ET.parse(svg).getroot()
    for path in root:
        value=path.get('fill','')
        if value.startswith('#'):
            rgb=tuple(bytes.fromhex(value[1:]));index=min(range(len(keys)),key=lambda i:sum((keys[i][j]-rgb[j])**2 for j in range(3)))
            r,g,b,a=colors[index];path.set('fill',f'#{r:02x}{g:02x}{b:02x}');path.set('opacity',f'{a/max_alpha*.8:.4f}')
    tile_height=650;tile_width=tile_height*width/height
    return '<g transform="scale('+str(tile_width/encoded.width)+')">'+''.join(ET.tostring(e,encoding='unicode') for e in root)+'</g>',tile_width,tile_height


def circle(x,y,r,fill):
    return f'<circle cx="{x}" cy="{y}" r="{r}" fill="{fill}"/>'


def animal_faces(kind):
    # Keep the original irregular camouflage body shapes, density and scale;
    # replace only the little face islands with rabbits or pandas.
    locations=[(70,210,.8,-35),(218,221,.85,85),(347,270,1.07,-15),(469,99,.98,8),(338,99,.62,-30),(119,308,.94,24),(119,416,1,35),(173,526,1.05,-36),(331,515,.96,-80),(464,370,1.03,-36),(537,516,.7,25)]
    face=''
    if kind=='panda':
        face+=circle(-31,-30,17,'#66838C')+circle(31,-30,17,'#66838C')
        face+='<ellipse cx="0" cy="0" rx="43" ry="36" fill="#DDE8E1"/>'
        face+='<ellipse cx="-17" cy="-2" rx="12" ry="16" transform="rotate(20 -17 -2)" fill="#66838C"/><ellipse cx="17" cy="-2" rx="12" ry="16" transform="rotate(-20 17 -2)" fill="#66838C"/>'
        face+=circle(-15,-4,3.5,'#ECF2E9')+circle(15,-4,3.5,'#ECF2E9')
        face+='<path d="M-7 14 Q0 9 7 14 Q5 22 0 22 Q-5 22-7 14Z M0 22 V28 M0 26 Q-6 33-10 27 M0 26 Q6 33 10 27" fill="#66838C" stroke="#66838C" stroke-width="2" stroke-linecap="round"/>'
    else:
        face+='<path d="M-29-14 C-56-82-25-101-15-29 C-18-105 17-105 13-19Z" fill="#E4E4ED" stroke="#8297AC" stroke-width="2"/>'
        face+='<path d="M-30-63 L-23-31 M-4-72 L-3-32" stroke="#B8A7C5" stroke-width="5" stroke-linecap="round"/>'
        face+='<ellipse cx="0" cy="0" rx="40" ry="34" fill="#E4E4ED"/>'
        face+=circle(-14,-1,4,'#7489A2')+circle(14,-1,4,'#7489A2')
        face+='<path d="M-5 12 Q0 8 5 12 L0 18Z M0 18 V23 M0 23 Q-6 29-10 24 M0 23 Q6 29 10 24" fill="#9C8EAB" stroke="#9C8EAB" stroke-width="2" stroke-linecap="round"/>'
        face+='<path d="M-25 13 L-37 10 M-25 18 L-37 19 M25 13 L37 10 M25 18 L37 19" stroke="#A6A9C2" stroke-width="1.5"/>'
    return ''.join(f'<g transform="translate({x} {y}) rotate({r}) scale({s})">{face}</g>' for x,y,s,r in locations)


PRESETS=[
    ('cyber-circuit','幻彩机核','幻彩机核.svg'),('cyber-hud','深空矩阵','幻彩机核.svg'),('cyber-city','霓虹脉冲','幻彩机核.svg'),
    ('nature-willow','夏绿成荫','精品尝鲜/夏绿成荫-浅色.png'),('nature-marigold','妙手生春','精品尝鲜/妙手生春-浅色.png'),('nature-trellis','花语涂鸦','神秘美学.svg'),
    ('animal-dragon','龙行绚烂','精品尝鲜/龙行绚烂-浅色.png'),('animal-rabbit','灵动玉兔','缤纷时刻.svg'),('animal-panda','熊猫漫游','缤纷时刻.svg')]


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('reference_folder',type=Path);folder=parser.parse_args().reference_folder
    records=[]
    for index,(id,title,ref) in enumerate(PRESETS):
        file=folder/ref
        width=height=650
        if file.suffix=='.png':
            drawing,width,height=trace_rgba(file)
        else:
            drawing=source_vector(file,['#829BAE','#7E9EB0','#9A8EAD'][index] if index<3 else '#9AAFA5')
            if index in (1,2):drawing=f'<g transform="rotate({index*90} 325 325)">{drawing}</g>'
            if index>=7:
                faces=animal_faces('rabbit' if index==7 else 'panda')
                drawing+=faces+f'<g transform="translate(-650 0)">{faces}</g><g transform="translate(650 0)">{faces}</g>'
        # Preserve each motif's reference aspect ratio. The boutique images and animal camouflage do not
        # match their edges exactly; paired mirror cells match their exact
        # edge values without a pale fade stripe or a stretched illustration.
        mirrored=file.suffix=='.png' or index>=7
        count=4 if mirrored else 3
        total=width*count
        # Only added faces need edge wrapping. Do not duplicate the complete
        # clipped reference outside the cell: that needlessly triples SVG work.
        tile=f'<svg id="cell" width="{width}" height="{height}" viewBox="0 0 {width} {height}" overflow="hidden"><use href="#motifs"/></svg>'
        copies=''.join(f'<use href="#cell" transform="translate({width*(i+1)} 0) scale(-1 1)"/>' if mirrored and i%2 else f'<use href="#cell" x="{width*i}"/>' for i in range(count))
        svg=f'<svg xmlns="{NS}" width="{total}" height="{height}" viewBox="0 0 {total} {height}"><title>{title}</title><desc>QQ reference derivative. Original motif proportions and composition retained; same-scale continuous cells across the width. Vector paths only.</desc><defs><g id="motifs">{drawing}</g>{tile}</defs>{copies}</svg>\n'
        target=OUT/(id+'.svg');target.write_text(svg)
        records.append(dict(id=id,title=title,file=target.name,reference=ref,referenceSha256=hashlib.sha256(file.read_bytes()).hexdigest(),sha256=hashlib.sha256(target.read_bytes()).hexdigest(),width=total,height=height,cellWidth=width,cellHeight=height,cells=count,mirrored=mirrored,format='SVG paths only; no embedded raster',modifications='Original composition and aspect ratio retained. PNG references traced with per-path alpha and paired mirror cells; vector references recolored, with face substitutions for rabbit/panda. Same-scale cells form the wide texture.'))
    (OUT/'sources.json').write_text(json.dumps(dict(referenceCollection='User-supplied QQ背景素材-20261030/原始背景纹理',rights='QQ reference derivatives for this development branch; no new stock/public-domain license is claimed.',presets=records),ensure_ascii=False,indent=2)+'\n')


if __name__=='__main__':main()
