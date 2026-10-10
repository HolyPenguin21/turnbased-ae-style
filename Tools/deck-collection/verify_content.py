"""Read-only content audit; does not pretend to execute Unity/C# gameplay tests."""
import argparse,json,re,subprocess
from pathlib import Path
import yaml
ROOT=Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--base',default='9c979237f53c2c300361425104b63c7d6aa54b2d',help='Original authoring revision; use HEAD only for an uncommitted snapshot workflow.')
REVISION=parser.parse_args().base
def parse(s):return yaml.safe_load('\n'.join(s.splitlines()[3:]))['MonoBehaviour']
def base(p):return parse(subprocess.check_output(['git','show',REVISION+':'+str(p.relative_to(ROOT))],cwd=ROOT,text=True))
keys={};legacy={};starter_totals={};checks=0
for p in (ROOT/'Assets/Cards').rglob('CardCatalog*.asset'):
 current=parse(p.read_text()); original=base(p)
 assert len(current['cards'])==len(original['cards'])
 for c,o in zip(current['cards'],original['cards']):
  k=c['authoredKey'];assert k and k not in keys,k;keys[k]=c
  legacy[original['displayName']+'/'+o['displayName']]=k
  assert c['deckPointCost']>=0 and c['deckCopyLimit']>=0
  assert not o.get('authoredKey') or o['authoredKey']==k
  assert {k:v for k,v in c.items() if k not in ['authoredKey','deckPointCost','deckCopyLimit','deckBuilderExcluded']}=={k:v for k,v in o.items() if k!='authoredKey'},o['displayName']
  checks+=1
starting=parse((ROOT/'Assets/Cards/StartingDeckCatalog.asset').read_text());old=base(ROOT/'Assets/Cards/StartingDeckCatalog.asset')
for d,o in zip(starting['decks'],old['decks']):
 assert len(d['cards'])==len(o['cards'])
 for e,oe in zip(d['cards'],o['cards']):
  assert e['cardKey'] in keys
  assert e['count']==oe['count']
  assert e['cardKey']==legacy.get(oe['cardKey'],oe['cardKey'])
 entries=[e for e in d['cards'] if e['count']>0]+next(x['cards'] for x in starting['collectionBlueprints'] if x['faction']==d['faction'])
 assert len({e['cardKey'] for e in entries})==len(entries)
 assert all(not keys[e['cardKey']]['deckBuilderExcluded'] and e['count']<=keys[e['cardKey']]['deckCopyLimit'] for e in entries)
 total=sum(keys[e['cardKey']]['deckPointCost']*e['count'] for e in entries);assert total<=100
 assert sum(keys[e['cardKey']]['cardType']==0 for e in entries)==8
 starter_totals[d['deckName']]=total
rp=parse((ROOT/'Assets/Cards/ResearchProductionCatalog.asset').read_text())
assert rp==base(ROOT/'Assets/Cards/ResearchProductionCatalog.asset')
for e in rp['researchCards']+rp['productionCards']:assert e['cardKey'] in keys
for p in (ROOT/'Assets/Cards').rglob('*.asset'):
 for key in re.findall(r'(?m)^\s*(?:-\s*)?cardKey: (.+)$',p.read_text()):assert key in keys,(p,key)
# Equivalent offline tag check, with the canonical FitsHost call retained in the Unity gate.
def tags(s):
 if isinstance(s,list):return [int(x) for x in s]
 return [int.from_bytes(bytes.fromhex(s[i:i+8]),'little') for i in range(0,len(s),8)] if s else []
for p in (ROOT/'Assets/Cards').rglob('CardCatalog*.asset'):
 raw=yaml.load('\n'.join(p.read_text().splitlines()[3:]),Loader=yaml.BaseLoader)['MonoBehaviour']
 for c in raw['cards']:
  keys[c['authoredKey']]['tags']=tags(c.get('unitTypeTags',''))
  if c['cardType']=='5':
   keys[c['authoredKey']]['hostTags']=tags(c['equipment']['hostTypeTags']);keys[c['authoredKey']]['hostKinds']=tags(c['equipment']['hostKinds'])
for d in starting['decks']:
 hosts=[keys[e['cardKey']] for e in d['cards'] if e['count']>0 and keys[e['cardKey']]['cardType'] in [0,1]]
 for b in next(x['cards'] for x in starting['collectionBlueprints'] if x['faction']==d['faction']):
  e=keys[b['cardKey']]
  assert any((1 if h['cardType']==0 else 0) in e['hostKinds'] and (e['attachmentSlot']!=1 or 0 in h['tags']) and (not e['hostTags'] or set(e['hostTags'])&set(h['tags'])) for h in hosts),b
print(json.dumps(dict(uniqueDefinitions=checks,starters=starter_totals,researchEntries=len(rp['researchCards']),productionEntries=len(rp['productionCards']),combatDataUnchanged=True,aiStarterCountsUnchanged=True,seedCompatibilityOffline=True),ensure_ascii=False,indent=2))
