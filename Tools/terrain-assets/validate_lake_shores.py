"""Check the authored olive-water footprint against the existing flat-top hex edges.

Single-hex lakes have closed shores on all six sides. The outer band must stay fixed
through the complete animation, including its loop. This is not Unity render QA.
"""
from pathlib import Path
import math
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
for biome in ("AridSteppe", "Desert"):
    paths=sorted((ROOT / "Assets/Textures/Terrain" / biome / "Complexes").glob("AcidLake_Part1_*.png"))
    assert len(paths)==7
    frames=[]
    worst_outer=0.0
    for path in paths:
        with Image.open(path) as image:
            image.verify()
        rgb=np.asarray(Image.open(path).convert("RGB"),dtype=float)
        frames.append(rgb)
        water=(rgb[...,1] > .92*rgb[...,0]) & (rgb[...,2] < .95*rgb[...,1])
        assert water[160:350,160:350].mean()>.25, f"Missing acid water: {path.name}"
        for edge in range(6):
            angle=math.radians(edge*60+30)
            normal=np.array([math.cos(angle),math.sin(angle)])
            tangent=np.array([-normal[1],normal[0]])
            points=.5+np.array([normal*math.sqrt(3)/4*depth+tangent*offset
                for depth in (.94,.97,.99) for offset in np.linspace(-.22,.22,89)])
            xy=np.clip(np.rint(points*511).astype(int),0,511)
            fraction=float(water[xy[:,1],xy[:,0]].mean())
            worst_outer=max(worst_outer,fraction)
            assert fraction<=.04, f"Water in exterior shore band: {path.name}, edge {edge}, {fraction:.3f}"
    yy,xx=np.mgrid[0:512,0:512]
    dx=(xx-255.5)/256;dy=(yy-255.5)/256
    norm=np.maximum.reduce([dx*math.cos(math.pi/6+i*math.pi/3)+dy*math.sin(math.pi/6+i*math.pi/3) for i in range(6)])/(math.sqrt(3)/2)
    changed=np.any(np.stack(frames)!=frames[0],axis=(0,3))
    assert changed.any(), f"No animation: {biome}"
    assert not changed[norm>=.65].any(), f"Animated shore or soil: {biome}"
    assert changed.mean()<.02, f"Animation affects broad surface: {biome}"
    print(f"{biome}: 7 frames, six closed shore edges, outer water max={worst_outer:.3f}; fixed shore/soil, localized bubbles ({changed.sum()} pixels)")
