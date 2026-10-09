"""Measure every hex in an exported complex, not just a pooled bright-ground median."""
from pathlib import Path
import json,sys,numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter,map_coordinates

root=Path(sys.argv[1]) if len(sys.argv)>1 else Path(__file__).resolve().parents[2]
manifest_path=root/'offsets.json'
if not manifest_path.exists():manifest_path=root/'Docs/terrain-complexes/directional-assets/offsets.json'
manifest=json.loads(manifest_path.read_text())
rows=[];seams=[]
for family,entry in manifest['families'].items():
 for variant in entry['variants']:
  assert len(variant['parts'])==({'Canyon':3,'GiantMachineWreck':2}[family])
  arrays=[]
  for i,p in enumerate(variant['parts']):
   a=np.asarray(Image.open(root/p['file']).convert('RGB'),dtype=float)
   arrays.append(a)
   h,w=a.shape[:2]; yy,xx=np.mgrid[:h,:w]
   dx=(xx+.5)/w*2-1; dy=(yy+.5)/h*2-1
   norms=np.maximum.reduce([dx*np.cos(np.pi/6+j*np.pi/3)+dy*np.sin(np.pi/6+j*np.pi/3) for j in range(6)])
   face=norms<=np.sqrt(3)/2
   warm=(a[:,:,0]>170)&(a[:,:,1]>120)&(a[:,:,0]-a[:,:,1]>20)&(a[:,:,1]-a[:,:,2]>25)&face
   dark=(a[:,:,0]<110)&(a[:,:,1]<100)&face
   luma=a[:,:,0]*.2126+a[:,:,1]*.7152+a[:,:,2]*.0722
   low=gaussian_filter(luma,sigma=12*w/512)
   rows.append({'family':family,'angle':variant['angle_degrees'],'part':i+1,
    'ground_rgb_mean':np.round(a[warm].mean(0),2).tolist(),
    'ground_luma_mean':round(float(luma[warm].mean()),2),
    'ground_luma_percentiles':np.round(np.percentile(luma[warm],[10,50,90]),2).tolist(),
    'ground_luma_std':round(float(luma[warm].std()),2),
    'broad_mottling_std':round(float(low[warm].std()),2),
    'dark_fraction_percent':round(float(dark.sum()/face.sum()*100),2)})
  if variant['angle_degrees']:
   xy=np.array([(p['offset']['x']*300,-np.sqrt(3)*200*(p['offset']['y']+p['offset']['x']/2)) for p in variant['parts']])
   for i in range(len(xy)):
    for j in range(i+1,len(xy)):
     delta=xy[j]-xy[i];distance=np.linalg.norm(delta)
     if abs(distance-200*np.sqrt(3))>1e-6:continue
     tangent=np.array([-delta[1],delta[0]])/distance
     points=(xy[i]+xy[j])/2+np.linspace(-90,90,181)[:,None]*tangent
     samples=[]
     for k in [i,j]:
      h,w=arrays[k].shape[:2];coords=(points-xy[k]+200)/400*np.array([w,h])-.5
      samples.append(np.stack([map_coordinates(arrays[k][:,:,c],[coords[:,1],coords[:,0]],order=1,mode='nearest') for c in range(3)],axis=1))
     diff=np.abs(samples[0]-samples[1]);connected=((samples[0][:,0]<110)&(samples[0][:,1]<100)&(samples[1][:,0]<110)&(samples[1][:,1]<100)).sum()
     seams.append({'family':family,'angle':variant['angle_degrees'],'parts':[i+1,j+1],'mean_rgb_error':round(float(diff.mean()),3),'p95_rgb_error':round(float(np.percentile(diff,95)),3),'connected_dark_edge_samples':int(connected)})
violations=[]
for s in seams:
 if s['connected_dark_edge_samples']<6:violations.append(f'{s["family"]} {s["angle"]} parts {s["parts"]}: foreground does not cross shared edge')
 if s['mean_rgb_error']>3 or s['p95_rgb_error']>12:violations.append(f'{s["family"]} {s["angle"]} parts {s["parts"]}: seam RGB mismatch')
for family in manifest['families']:
 ref=[r for r in rows if r['family']==family and r['angle']==0]
 rgb=np.mean([r['ground_rgb_mean'] for r in ref],axis=0)
 lum=np.mean([r['ground_luma_mean'] for r in ref])
 for r in [r for r in rows if r['family']==family and r['angle']]:
  if family=='Canyon' and not 6<=r['dark_fraction_percent']<=23:
   violations.append(f'{family} {r["angle"]} part {r["part"]}: dark fraction {r["dark_fraction_percent"]}%')
  if family=='GiantMachineWreck' and not 8<=r['dark_fraction_percent']<=45:
   violations.append(f'{family} {r["angle"]} part {r["part"]}: dark fraction {r["dark_fraction_percent"]}%')
  if np.max(np.abs(np.array(r['ground_rgb_mean'])-rgb))>4:
   violations.append(f'{family} {r["angle"]} part {r["part"]}: RGB mean outside 4-level tolerance')
  if abs(r['ground_luma_mean']-lum)>3:
   violations.append(f'{family} {r["angle"]} part {r["part"]}: brightness outside 3-level tolerance')
report={'method':'Actual flat-top face; warm ground mask identical across variants. Shared-edge foreground continuity and RGB sampling. Thresholds are diagnostics and do not replace image inspection.','rows':rows,'seams':seams,'violations':violations}
(manifest_path.parent/'visual-consistency.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps({'violations':violations,'parts':len(rows)},indent=2))
sys.exit(bool(violations))
