"""Offline one-time authored content migration; preserves YAML formatting and asset GUIDs.
Requires PyYAML. Run only against the original revision; verification is a separate tool.
"""
import re, math, csv
from pathlib import Path
import yaml
ROOT=Path(__file__).resolve().parents[2]
paths=list((ROOT/'Assets/Cards').rglob('CardCatalog*.asset'))
all_cards={}; legacy={}; reports=[]; catalog_cards={}
def slug(s):return re.sub(r'[^a-z0-9]+','-',s.lower()).strip('-')
def load(p):return yaml.safe_load('\n'.join(p.read_text().splitlines()[3:]))['MonoBehaviour']
# Refuse a second run before touching any authored data.
if any('deckPointCost' in c for p in paths for c in load(p)['cards']):
 raise SystemExit('Content already migrated; use verify_content.py instead.')
(ROOT/'Docs/DeckCollection').mkdir(parents=True,exist_ok=True)
for path in paths:
 d=load(path); faction=d['faction']; prefix={0:'concord',4:'ashen',5:'vessels',3:'neutral'}[faction]
 blocks=re.split(r'(?=^  - id:)',path.read_text(),flags=re.M); cards=[]
 for block in blocks[1:]:
  c=yaml.safe_load('MonoBehaviour:\n  cards:\n'+block)['MonoBehaviour']['cards'][0]
  key=c.get('authoredKey') or f'{prefix}.card.{slug(c["displayName"])}'
  legacy[d['displayName']+'/'+c['displayName']]=key
  c['authoredKey']=key
  if key in all_cards:raise ValueError('Duplicate '+key)
  typ=c['cardType'];abilities=c.get('grantedAbilities') or []
  excluded=(typ==4 or faction in (0,4,5) and c['id']==0 or faction==3 and typ==1)
  combat=.8*c['attack']+.7*c['defenseRating']+.4*c['hitPoints']+.7*c['range']+.4*c['initiative']
  recce=max([int(m[1]) for a in abilities if (m:=re.match(r'r(\d+)s\d+',a))]+[0])
  strategic=.3*c['moveMax']+recce+sum(1 for a in set(abilities) if a in ['Stealth','RapidReaction','Capture'])
  economy=sum(1 for a in set(abilities) if a in ['Researcher','Assembler','Collector','Builder','Barracks','Research','Production','CollectHuman','CollectEnergy','CollectMaterials','CollectTech'])
  if typ==0:cost=math.ceil(1+c['commandRating']*.16+c['fate']*.12+economy*.25+c['initiative']*.08)
  elif typ==1:cost=max(1,math.ceil((combat+strategic+economy)*.13))
  elif typ in (2,3):cost=1 if typ==2 else 2
  elif typ==5:
   g=c['equipment']; changes=g.get('statChanges') or []
   score=sum(max(0,x['amount']) for x in changes if not x.get('isOverride'))
   score+=2*len(set(g.get('addAbilities') or [])); cost=max(1,math.ceil(score/4))
  else:cost=0
  if excluded:cost=0
  c['deckPointCost']=cost;c['deckCopyLimit']=0 if excluded else 4;c['deckBuilderExcluded']=int(excluded)
  all_cards[key]=c;cards.append(c)
  block=re.sub(r'^    authoredKey:.*$',f'    authoredKey: {key}\n    deckPointCost: {cost}\n    deckCopyLimit: {c["deckCopyLimit"]}\n    deckBuilderExcluded: {int(excluded)}',block,flags=re.M)
  blocks[len(cards)]=block
  if not excluded:reports.append(dict(CardKey=key,Faction='Shared' if faction==3 else prefix,CardType=typ,AttachmentSlot=c.get('attachmentSlot',0),CombatUtility=round(combat,2),StrategicUtility=round(strategic,2),EconomicUtility=economy,DeploymentConstraints=f'AP {c["apCost"]}; activation {c["activationApCost"]}; resources {c["resourceCost"]}; building {c.get("requiredBuildingAbility")}; aviation {c.get("isAviation")}',RecommendedPointCost=cost,FinalPointCost=cost,CopyLimit=4,Rationale='Fixed authored baseline; host-dependent effects require Unity calibration report/playtesting.'))
 path.write_text(''.join(blocks));catalog_cards[faction]=cards
legacy.update({'Iron Concord/AA Crawler': 'concord.card.crawler', 'Neutral/it Heavy MG': 'neutral.equipment.bio.heavy-mg', 'Neutral/it Armor Plate': 'neutral.equipment.mechanical.armor-plate', 'Neutral/it AT Launcher': 'neutral.equipment.bio.at-launcher', 'Neutral/it Nuclear Engine': 'neutral.equipment.mechanical.nuclear-engine', 'Neutral/it Plasma Gun': 'neutral.equipment.bio.plasma-gun', 'Neutral/it Double Barrel': 'neutral.equipment.armored.double-barrel', 'Neutral/it Plasma Cannon': 'neutral.equipment.armored.plasma-cannon'})
# Migrate qualified references in ALL participating authored catalogs, not display names.
for path in (ROOT/'Assets/Cards').rglob('*.asset'):
 s=path.read_text()
 for old,new in legacy.items():
  s=re.sub(r'(?m)^(\s*(?:-\s*)?(?:cardKey|raiseTheRotsUnitKey): )'+re.escape(old)+r'\s*$',lambda m:m[1]+new,s)
 path.write_text(s)
start=ROOT/'Assets/Cards/StartingDeckCatalog.asset'
s=start.read_text();seed=['neutral.equipment.infantry.ballistic-shield','neutral.equipment.infantry.optical-scope','neutral.equipment.mechanical.armor-plate','neutral.mutator.dermal-plating','neutral.mutator.adrenal-surge','neutral.mutator.enhanced-senses']
s+='  collectionBlueprints:\n'
for f in [0,4,5]:
 s+=f'  - deckName: Initial blueprints\n    faction: {f}\n    cards:\n'
 for key in (seed[:3] if f==5 else seed):s+=f'    - cardKey: {key}\n      count: 1\n'
start.write_text(s)
with (ROOT/'Docs/DeckCollection/card-costs.csv').open('w') as out:
 writer=csv.DictWriter(out,fieldnames=list(reports[0]));writer.writeheader();writer.writerows(reports)
d=load(start)
lines=['# Initial collection and fixed costs','', 'The AI starter counts are unchanged. No combat field or existing nonempty key was changed.','', 'Point values are an initial authored calibration, not validated competitive balance. The Unity report uses EquipmentSystem compatibility/projections and EquipmentEfficiency on actual hosts.','']
for deck in d['decks']:
 entries=[e for e in deck['cards'] if e['count']>0]+[dict(cardKey=k,count=1) for k in (seed[:3] if deck['faction']==5 else seed)]
 points=sum(all_cards[e['cardKey']]['deckPointCost']*e['count'] for e in entries)
 assert points<=100,(deck['deckName'],points)
 assert all(e['count']<=all_cards[e['cardKey']]['deckCopyLimit'] for e in entries)
 lines += [f'## {deck["deckName"]}: {points}/100','', '| Card key | Initial copies | Points each |','|---|---:|---:|']+[f'| {e["cardKey"]} | {e["count"]} | {all_cards[e["cardKey"]]["deckPointCost"]} |' for e in entries]+['']
 print(deck['deckName'],points)
(ROOT/'Docs/DeckCollection/initial-collections.md').write_text('\n'.join(lines))
# Scene composition references existing assets only.
config=ROOT/'Assets/Config/GameConfig.asset'
guid=lambda p:re.search(r'guid: (\w+)',(ROOT/p).read_text())[1]
s=config.read_text();s=s.replace('  eventMarkerPrefab:',f'  collectionDeckCatalog: {{fileID: 11400000, guid: {guid("Assets/Cards/StartingDeckCatalog.asset.meta")}, type: 2}}\n  collectionResearchCatalog: {{fileID: 11400000, guid: {guid("Assets/Cards/ResearchProductionCatalog.asset.meta")}, type: 2}}\n  eventMarkerPrefab:',1);config.write_text(s)
menu=ROOT/'Assets/Scenes/MainMenu.unity';s=menu.read_text().replace('  settingsButton: {fileID: 1950000089}', '  settingsButton: {fileID: 1950000089}\n  gameConfig: {fileID: 11400000, guid: '+guid('Assets/Config/GameConfig.asset.meta')+', type: 2}');menu.write_text(s)
