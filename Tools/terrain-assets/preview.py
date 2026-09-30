"""Offline diagnostic of texture alignment and the existing hex edge fade.
NOT a Unity screenshot: does not emulate URP colour space, camera tilt, fog or markers.
"""
from pathlib import Path
import math
import numpy as np
from PIL import Image
ROOT=Path(__file__).resolve().parents[2]
TEX=ROOT/'Assets/Textures/Terrain/Desert'
OUT=ROOT/'Docs/terrain-complexes'
OUT.mkdir(parents=True,exist_ok=True)

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
    Image.fromarray(np.clip(rgb,0,255).astype('uint8')).save(OUT/name)
render({(0,0):('Complexes/AcidLake_Part1_00.png',0),(1,0):('Complexes/AcidLake_Part2_00.png',0)},'lake-offline-diagnostic.png')
render({(0,-1):('Complexes/Canyon_Part1.png',0),(1,-1):('Complexes/Canyon_Part2.png',0),(1,0):('Complexes/Canyon_Part3.png',0)},'canyon-offline-diagnostic.png')
