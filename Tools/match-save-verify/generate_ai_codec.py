#!/usr/bin/env python3
"""Regenerate concrete AI JSON DTOs/mappers from the checked-in typed schema manifest.

The manifest records explicit field types; generated model/DTO mappings use no reflection. Keep schema
field order stable: numeric suffixes are stable source identifiers for generated helper types.
Adding fields requires updating this manifest and the matching exact snapshot constructor.
"""
import json,re
from pathlib import Path
repo=Path(__file__).resolve().parents[2]
j=json.loads((repo/'Tools/match-save-verify/ai-types.json').read_text())
root='global::Game.Ai.AiPersistentState<string>'
# Get-only target values have explicit tagged factory decoders below.
for name,fields in {
 'AttackTargetRef':[('HasValue','bool'),('Hex','global::Game.HexGrid.HexCoord'),('ExpectedOwner','global::Game.Players.PlayerSetupData'),('Kind','global::Game.Ai.V2.AttackTargetKind')],
 'RaidTargetRef':[('HasValue','bool'),('Kind','global::Game.Ai.V2.RaidTargetKind'),('ArmyId','int'),('Hex','global::Game.HexGrid.HexCoord')]
}.items():j['global::Game.Ai.V2.'+name]['fields']=[dict(name=n,type=t,readOnly=False)for n,t in fields]
# Explicit semantic adapters: private role bits and registry delegates.
role='global::Game.Ai.V2.RoleCoverage';pred='global::System.Func<global::Game.Combat.WorthIt.DefenderProfile, bool>'
j[role]['kind']='roles';j[pred]['kind']='predicate'
# Objective generic placeholder is only used for the mission payload field.
for k,v in j.items():
 if v['kind']=='object' and v.get('name')=='IntentState':
  for f in v['fields']:
   if f['name']=='Objective':f['type']='objective'
j['objective']={'kind':'objective'}
keys=list(j);names={};idx={k:i for i,k in enumerate(keys)}
for k,v in j.items():
 if k==root:n='MatchAiData'
 elif v['kind']=='primitive':n={'global::Game.Players.PlayerSetupData':'MatchAiPlayerRef','global::Game.Map.ArmyData':'MatchAiArmyRef','global::Game.Units.UnitData':'MatchAiUnitRef','global::Game.Cards.CardData':'MatchAiCardRef'}.get(k,k)
 elif v['kind']=='enum':n='int'
 elif v['kind']=='predicate':n='string'
 elif v['kind']=='objective':n='MatchAiObjectiveData'
 else:
  base=v.get('name') or {'dict':'Map','list':'List','array':'Array','tuple':'Tuple','nullable':'Optional','roles':'Roles'}[v['kind']]
  n='MatchAi'+base+str(idx[k])
 names[k]=n

def rt(k):return re.sub(r'(AiPersistentState|MissionIntentState\.(?:PersistentState|IntentState))<string>',r'\1<MatchAiObjectiveData>',k)
def m(k,decode=False):return ('Read' if decode else 'Write')+str(idx[k])
def fields(k):return j[k].get('fields',[])
def call(k,x,dec=False):return f'{m(k,dec)}({x}, c)'
def dtofield(k,f):return f'public {names[f["type"]]} {f["name"]};'

out=['// Generated concrete JSON DTO schema. See Tools/match-save-verify/generate_ai_codec.py.','using System;','using System.Collections.Generic;','namespace Game.Saves','{']
for k,v in j.items():
 kind=v['kind'];n=names[k]
 if kind in ['enum','primitive','predicate','objective']:continue
 out+=['    // '+k,'    [Serializable] internal sealed class '+n,'    {','        public bool HasData;']
 if kind=='object':
  out+=['        '+dtofield(k,f) for f in fields(k)]
  if k==root:out+=['        public List<int> HistoricalArmyIds = new List<int>();','        public List<int> HistoricalUnitIds = new List<int>();','        public List<int> HistoricalCardIds = new List<int>();']
 elif kind=='tuple':out+=['        '+dtofield(k,f)for f in fields(k)]
 elif kind=='nullable':out+=['        public '+names[v['args'][0]]+' Value;']
 elif kind=='roles':out+=['        public List<int> Values = new List<int>();']
 elif kind=='dict':
  a,b=v['args'];out+=['        [Serializable] public sealed class Entry { public '+names[a]+' Key; public '+names[b]+' Value; }','        public List<Entry> Entries = new List<Entry>();']
 else:out+=['        public List<'+names[v.get('element')or v['args'][0]]+'> Values = new List<'+names[v.get('element')or v['args'][0]]+'>();']
 out+=['    }']
out+=['}'];(repo/'Assets/Scripts/Saves/MatchAiData.cs').write_text('\n'.join(out)+'\n')

# Append exact reconstruction constructors only when readonly fields require them.
mods={};seen=set()
for k,v in j.items():
 if v['kind']!='object' or not any(f.get('readOnly')for f in fields(k)) or not v.get('file'):continue
 ident=(v['file'],v['insert'])
 if ident in seen:continue
 seen.add(ident);fs=v['originalFields'];ctor='\n        // Exact detached save reconstruction; no planning, registration or normalization.\n        internal '+v['name']+'(bool __matchSave, '+', '.join(f['type']+' __'+f['name'] for f in fs)+')\n        {\n'+''.join('            '+f['name']+' = __'+f['name']+';\n'for f in fs)+'        }\n'
 mods.setdefault(v['file'],[]).append((v['insert'],ctor,v['name']))
for file,edits in mods.items():
 p=repo/file;s=p.read_text()
 # use refreshed syntax positions by finding the relevant class/struct; insertion is robust to unrelated parallel edits.
 for pos,ctor,name in sorted(edits,reverse=True):
  if f'{name}(bool __matchSave,' in s:continue
  match=re.search(r'\b(?:class|struct)\s+'+name+r'(?:<[^>{}]+>)?\b[^{}]*\{',s)
  if not match:raise Exception(name)
  s=s[:match.end()]+ctor+s[match.end():]
 p.write_text(s)

out=['// Generated explicit typed mappers; runtime serialization does not inspect types.','using System;','using System.Linq;','using System.Collections.Generic;','using Game.Ai;','using Game.Ai.V2;','namespace Game.Saves','{','    internal static partial class MatchAiCodec','    {']
for k,v in j.items():
 kind=v['kind'];n=names[k];t=rt(k)
 if kind=='objective':continue
 out+=['        private static '+n+' '+m(k)+'('+t+' value, Context c)','        {']
 if kind=='primitive':
  if k in ['float','double']:out+=['            return Finite(value);']
  elif k.startswith('global::Game.'):
   fun={'MatchAiPlayerRef':'Player','MatchAiArmyRef':'Army','MatchAiUnitRef':'Unit','MatchAiCardRef':'Card'}[n];out+=['            return c.Write'+fun+'(value);']
  else:out+=['            return value;']
 elif kind=='enum':out+=['            return (int)value;']
 elif kind=='predicate':out+=['            return WritePredicate(value);']
 elif kind=='roles':out+=['            return new '+n+' { HasData = true, Values = Enum.GetValues(typeof(IntendedRole)).Cast<IntendedRole>().Where(value.Has).Select(x => (int)x).ToList() };']
 elif kind=='nullable':out+=['            return new '+n+' { HasData = value.HasValue, Value = value.HasValue ? '+call(v['args'][0],'value.Value')+' : default };']
 elif kind=='tuple':
  for f in fields(k):
   if 'ArmyId' in f['name']:
    if f['type']=='int':out+=['            c.RecordArmy(value.'+f['name']+');']
    elif j[f['type']]['kind']=='nullable':out+=['            if (value.'+f['name']+'.HasValue) c.RecordArmy(value.'+f['name']+'.Value);']
   if f['name']=='CardId':out+=['            c.RecordCard(value.'+f['name']+');']
  out+=['            return new '+n+' { HasData = true, '+', '.join(f['name']+' = '+call(f['type'],'value.'+f['name'])for f in fields(k))+' };']
 elif kind=='object':
  if not v['isValue']:out+=['            if (value == null) return new '+n+'();']
  # Record integer-only identities, including historical ones. Exact references remain separate tagged refs.
  for f in fields(k):
   fname=f['name'];ft=f['type']
   label='Army' if 'ArmyId' in fname or fname in ['DestroyedArmies','GatherSupportArmyIds'] else 'Unit' if 'RuntimeId' in fname or fname=='ActivationCoveredUnitRuntimeIds' else None
   if label:
    if ft=='int':out+=['            c.Record'+label+'(value.'+fname+');']
    elif j[ft]['kind']=='nullable' and j[ft]['args'][0]=='int':out+=['            if (value.'+fname+'.HasValue) c.Record'+label+'(value.'+fname+'.Value);']
    elif j[ft]['kind']in['list','array']:out+=['            if (value.'+fname+' != null) foreach (int id in value.'+fname+') c.Record'+label+'(id);']
  out+=['            return new '+n+' { HasData = true, '+', '.join(f['name']+' = '+call(f['type'],'value.'+f['name'])for f in fields(k))+' };']
 elif kind=='dict':
  a,b=v['args'];out+=['            return new '+n+' { HasData = value != null, Entries = value == null ? new List<'+n+'.Entry>() : value.Select(x => new '+n+'.Entry { Key = '+call(a,'x.Key')+', Value = '+call(b,'x.Value')+' }).ToList() };']
 else:
  elem=v.get('element')or v['args'][0];out+=['            return new '+n+' { HasData = value != null, Values = value == null ? new List<'+names[elem]+'>() : value.Select(x => '+call(elem,'x')+').ToList() };']
 out+=['        }','        private static '+t+' '+m(k,True)+'('+n+' data, Context c)','        {']
 if kind=='primitive':
  if k in ['float','double']:out+=['            return Finite(data);']
  elif k.startswith('global::Game.'):
   fun={'MatchAiPlayerRef':'Player','MatchAiArmyRef':'Army','MatchAiUnitRef':'Unit','MatchAiCardRef':'Card'}[n];out+=['            return c.Read'+fun+'(data);']
  else:out+=['            return data;']
 elif kind=='enum':out+=['            return CheckedEnum<'+t+'>(data);']
 elif kind=='predicate':out+=['            return ReadPredicate(data);']
 else:
  out+=['            Required(data);']
  if kind=='roles':out+=['            Required(data.Values); var result = RoleCoverage.None; var seen = new HashSet<int>();','            foreach (int value in data.Values) { if (!seen.Add(value)) throw Invalid("Duplicate coverage role"); result = result.With(CheckedEnum<IntendedRole>(value)); }','            return result;']
  elif kind=='nullable':out+=['            return data.HasData ? ('+t+')'+call(v['args'][0],'data.Value',True)+' : null;']
  elif kind=='tuple':out+=['            if (!data.HasData) throw Invalid("Missing tuple");','            return ('+', '.join(call(f['type'],'data.'+f['name'],True)for f in fields(k))+');']
  elif kind=='object':
   out+=['            if (!data.HasData) '+('throw Invalid("Missing value");' if v['isValue'] else 'return null;')]
   if v.get('name')=='AttackTargetRef':out+=['            var kind = '+call('global::Game.Ai.V2.AttackTargetKind','data.Kind',True)+';','            if (!data.HasValue) return AttackTargetRef.None;','            var owner = '+call('global::Game.Players.PlayerSetupData','data.ExpectedOwner',True)+';','            if (owner == null) throw Invalid("Missing attack owner");','            return AttackTargetRef.For('+call('global::Game.HexGrid.HexCoord','data.Hex',True)+', owner, kind);']
   elif v.get('name')=='RaidTargetRef':out+=['            var kind = '+call('global::Game.Ai.V2.RaidTargetKind','data.Kind',True)+';','            return !data.HasValue ? RaidTargetRef.None : kind == RaidTargetKind.NeutralArmy ? RaidTargetRef.ForNeutralArmy(data.ArmyId) : RaidTargetRef.ForEventGuard('+call('global::Game.HexGrid.HexCoord','data.Hex',True)+');']
   elif any(f.get('readOnly')for f in fields(k)):out+=['            return new '+t+'(true, '+', '.join(call(f['type'],'data.'+f['name'],True)for f in fields(k))+');']
   else:
    fs=fields(k)
    # Raid phase setter invalidates its cycle clock, so restore that clock last.
    if v.get('name')=='RaidIntent':fs=sorted(fs,key=lambda f:f['name']=='ReinforcementRequestedTurn')
    out+=['            return new '+t+' { '+', '.join(f['name']+' = '+call(f['type'],'data.'+f['name'],True)for f in fs)+' };']
  elif kind=='dict':
   a,b=v['args'];out+=['            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }','            var result = new Dictionary<'+rt(a)+', '+rt(b)+'>();','            foreach (var entry in data.Entries) { Required(entry); result.Add('+call(a,'entry.Key',True)+', '+call(b,'entry.Value',True)+'); }','            return result;']
  else:
   elem=v.get('element')or v['args'][0];tail='.ToArray()'if kind=='array'else'.ToList()';out+=['            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }','            return data.Values.Select(x => '+call(elem,'x',True)+')'+tail+';']
 out+=['        }']
out+=['    }','}'];(repo/'Assets/Scripts/Saves/MatchAiCodec.Generated.cs').write_text('\n'.join(out)+'\n')


print('root methods',m(root),m(root,True))
for name in ['ScoutIntent','RaidIntent','EconomyIntent','DevelopmentIntent','ActiveDefenceIntent','AttackIntent']:print(name,names['global::Game.Ai.V2.'+name],m('global::Game.Ai.V2.'+name),m('global::Game.Ai.V2.'+name,True))
