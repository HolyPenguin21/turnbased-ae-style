from pathlib import Path
import yaml,json,re,struct,sys
ENUM_ARRAYS={'unitTypeTags','hostTypeTags','hostKinds','clearAbilityFamilies'}
def convert(v,key=''):
 if isinstance(v,dict):return {k:convert(val,k) for k,val in v.items()}
 if isinstance(v,list):return [convert(val) for val in v]
 if key in ENUM_ARRAYS:
  return [struct.unpack('<i',bytes.fromhex(v[i:i+8]))[0] for i in range(0,len(v),8)] if v else []
 if key=='isOverride':return v=='1'
 if isinstance(v,str) and re.fullmatch(r'-?\d+',v):return int(v)
 return v

def read(p):
 s='\n'.join(l for l in Path(p).read_text().splitlines() if not l.startswith('%') and not l.startswith('---'))
 return convert(yaml.load(s,Loader=yaml.BaseLoader)['MonoBehaviour'])
cards={}
for p in Path('Assets/Cards').rglob('*.asset'):
 try:d=read(p)
 except Exception:continue
 for c in d.get('cards',[]) or []:
  if not isinstance(c,dict) or 'authoredKey' not in c:continue
  c=dict(c);c.pop('art',None);c.pop('detailArt',None)
  c['grantedAbilities']=c.get('grantedAbilities') or []
  for k in ['removeAbilities','addAbilities','statChanges']:c['equipment'][k]=c['equipment'].get(k) or []
  cards[c['authoredKey']]=c
source=read('Assets/Cards/StartingDeckCatalog.asset');research=read('Assets/Cards/ResearchProductionCatalog.asset');decks=[]
for d in source['decks']:
 main=[cards[e['cardKey']] for e in d['cards'] for _ in range(e['count'])];attachments=[]
 for entries in [research['productionCards'],research['researchCards']]:
  for e in entries:
   if e['factionRestriction'] not in [2,d['faction']]:continue
   c=cards[e['cardKey']]
   if c['cardType']==5 and not any(x['authoredKey']==c['authoredKey'] for x in attachments):attachments.append(c)
 decks.append(dict(Name=d['deckName'],Faction=d['faction'],Main=main,Attachments=attachments))
Path(sys.argv[1]).write_text(json.dumps(decks))
