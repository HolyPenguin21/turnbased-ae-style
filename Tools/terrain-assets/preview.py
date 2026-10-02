"""Offline diagnostic of texture alignment and the existing hex edge fade.
NOT a Unity screenshot: does not emulate URP colour space, camera tilt, fog or markers.
"""
from pathlib import Path
import math
import argparse
import re
import yaml
import numpy as np
from PIL import Image, ImageDraw
ROOT=Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser()
parser.add_argument('--biome',choices=['Desert','AridSteppe'],default='Desert')
parser.add_argument('--group', choices=['AcidLake', 'Canyon', 'BoilingMud', 'GiantMachineWreck'])
args=parser.parse_args()
TEX=ROOT/'Assets/Textures/Terrain'/args.biome
OUT=ROOT/'Docs/terrain-complexes'
OUT.mkdir(parents=True,exist_ok=True)

raw=(ROOT/'Assets/Config/GameConfig.asset').read_text()
settings=yaml.safe_load('\n'.join(line for line in raw.splitlines() if not line.startswith(('%','---'))))['MonoBehaviour']['mapGeneration']
palette=settings if args.biome=='AridSteppe' else settings['desertOverride']
names={'AcidLake':'Acid lake','Canyon':'Deep canyon','BoilingMud':'Boiling mud field','GiantMachineWreck':'Giant machine wreck'}
templates={family:next(t for t in palette['complexes'] if t['terrainName']==name) for family,name in names.items()}
guids={re.search(r'^guid: ([0-9a-f]+)$',meta.read_text(),re.M)[1]:Path(str(meta)[:-5]).relative_to(TEX).as_posix() for meta in TEX.rglob('*.png.meta')}
def configured_parts(family,frame=0):
    return {(part['offset']['x'],part['offset']['y']):(guids[part['frames'][frame]['guid']],0) for part in templates[family]['parts']}

def render(parts,name):
    coords=[(q,r) for q in range(-3,4) for r in range(-3,4) if abs(q+r)<=3]
    radius=64; size=660
    rgb=np.full((size,size,3),[.29,.26,.22],dtype=float)*255
    for q,r in coords:
        cx=size/2+q*1.5*radius; cy=size/2-math.sqrt(3)*(r+q/2)*radius
        x0=max(0,int(cx-radius));x1=min(size,int(cx+radius)+1)
        y0=max(0,int(cy-radius));y1=min(size,int(cy+radius)+1)
        yy,xx=np.mgrid[y0:y1,x0:x1]
        dx=(xx+.5-cx)/radius; dz=(cy-yy-.5)/radius
        norm=np.maximum.reduce([dx*math.cos(math.pi/6+i*math.pi/3)+dz*math.sin(math.pi/6+i*math.pi/3) for i in range(6)])/(math.sqrt(3)/2)
        alpha=(1-.95*np.clip((norm-.85)/.15,0,1))*(norm<=1)
        file,rotation=parts.get((q,r),('Desert_01.png',0))
        texture=np.array(Image.open(TEX/file).convert('RGB'))
        angle=-rotation*math.pi/3
        ux=.5+(dx*math.cos(angle)-dz*math.sin(angle))/2
        uy=.5+(dx*math.sin(angle)+dz*math.cos(angle))/2
        tx=np.clip(np.rint(ux*(texture.shape[1]-1)).astype(int),0,texture.shape[1]-1)
        ty=np.clip(np.rint((1-uy)*(texture.shape[0]-1)).astype(int),0,texture.shape[0]-1)
        rgb[y0:y1,x0:x1]=rgb[y0:y1,x0:x1]*(1-alpha[...,None])+texture[ty,tx]*alpha[...,None]
    result=Image.fromarray(np.clip(rgb,0,255).astype('uint8'))
    if name is not None: result.save(OUT/(args.biome.lower()+'-'+name))
    return result
if args.group in (None, 'AcidLake'): render(configured_parts('AcidLake'),'lake-offline-diagnostic.png')
if args.group in (None, 'Canyon'): render(configured_parts('Canyon'),'canyon-offline-diagnostic.png')

for family, label in [('AcidLake', 'lake'), ('BoilingMud', 'mud')]:
    if args.group not in (None, family): continue
    frames=[]
    sheet=Image.new('RGB', (1320, 708), (30,30,30))
    draw=ImageDraw.Draw(sheet)
    for frame in range(7):
        image=render(configured_parts(family,frame),f'{label}-frame-00-offline.png' if frame == 0 else None)
        frames.append(image)
        x=(frame%4)*330; y=(frame//4)*354
        sheet.paste(image.resize((330,330),Image.Resampling.LANCZOS),(x,y))
        draw.text((x+8,y+334),f'{args.biome} {family} frame {frame:02} (offline)',fill=(240,240,240))
    frames[0].save(OUT/(args.biome.lower()+f'-{label}-offline-animation.gif'),save_all=True,append_images=frames[1:],duration=round(1000/templates[family]['framesPerSecond']),loop=0)
    sheet.save(OUT/(args.biome.lower()+f'-{label}-all-frames-offline.png'),optimize=True)
if args.group in (None, 'GiantMachineWreck'): render(configured_parts('GiantMachineWreck'),'wreck-offline-diagnostic.png')

