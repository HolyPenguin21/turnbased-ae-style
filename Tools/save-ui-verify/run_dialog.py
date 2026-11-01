#!/usr/bin/env python3
"""Execute production dialog slot data flow; native drawing/focus alone are adapted in a disposable copy."""
import json, os, re, shutil, subprocess, tempfile
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
dotnet = Path(os.environ['DOTNET']).resolve()
sdk = sorted((dotnet.parent/'sdk').glob('8.*'))[-1]
refs = sorted((dotnet.parent/'packs/Microsoft.NETCore.App.Ref').glob('8.*'))[-1]/'ref/net8.0'
cache = Path(os.environ['NUGET_PACKAGES'])
engine = cache/'unityengine.modules/2021.3.33/lib/netstandard2.0'
ui = next((cache/'unity3d.unityengine.ui/2020.3.21').rglob('UnityEngine.UI.dll'))
work = Path(os.environ.get('SAVE_DIALOG_VERIFY_WORK', tempfile.mkdtemp(prefix='save-dialog-')))
source = work/'runtime'
shutil.copytree(ROOT/'Assets/Scripts', source)
for path in source.rglob('*.cs'):
    text = path.read_text().replace('FindObjectsInactive.Include, FindObjectsSortMode.None', 'FindObjectsSortMode.None')
    text = re.sub(r'(FindObjectsByType<[^>]+>\()FindObjectsInactive\.(?:Include|Exclude)(\))', r'\1FindObjectsSortMode.None\2', text)
    path.write_text(text)
def body(text, signature, replacement):
    start = text.index('{', text.index(signature)); end = start+1; depth = 1
    while depth:
        if text[end] == '{': depth += 1
        elif text[end] == '}': depth -= 1
        end += 1
    return text[:start]+'{'+replacement+'}'+text[end:]
path = source/'UI/Panels/MatchSaveSlotsUI.cs'
text = path.read_text()
text = body(text, 'private void Draw()', '')
text = body(text, 'private void CancelDelete()', 'deleting = -1;')
text = text.replace('cancel.Select();', '')
path.write_text(text)
stub = (ROOT/'Tools/ai-verify/EngineStubs.cs').read_text()
stub = stub.replace('    public enum TextAlignmentOptions', '    public enum VerticalAlignmentOptions { Top,Middle,Bottom,Baseline,Geometry,Capline }\n    public enum TextAlignmentOptions')
stub = stub.replace('public TextAlignmentOptions alignment { get; set; }', 'public TextAlignmentOptions alignment { get; set; } public VerticalAlignmentOptions verticalAlignment {get;set;}')
stub = stub.replace('public TMP_Text captionText { get; set; }', 'public TMP_Text captionText { get; set; } public TMP_Text itemText {get;set;} public UnityEngine.RectTransform template {get;set;}')
(work/'EngineStubs.cs').write_text(stub)
program = work/'Program.cs'
program.write_text('''using System;using System.IO;using System.Linq;using System.Reflection;using System.Collections.Generic;
using System.Runtime.CompilerServices;using Game.UI;using Game.Saves;using TMPro;
class Program {
[System.Runtime.InteropServices.DllImport("libc")] static extern int chmod(string path,int mode);
static void Set(object target,string field,object value)=>target.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);
static T New<T>() where T:class=>(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
static MatchSaveSlotsUI Dialog(string dir,bool saving,int selected){var dialog=New<MatchSaveSlotsUI>();
Set(dialog,"application",new MatchSaveApplication(dir,null,null,null,null));Set(dialog,"occupiedSlots",new Dictionary<int,MatchSaveSlot>());Set(dialog,"saving",saving);Set(dialog,"slots",Enumerable.Range(0,9).ToList());
Set(dialog,"selected",selected);Set(dialog,"deleting",selected);Set(dialog,"status",New<TMP_Text>());
Set(dialog,"<IsShowing>k__BackingField",true);return dialog;}
static void Confirm(MatchSaveSlotsUI dialog)=>typeof(MatchSaveSlotsUI).GetMethod("ConfirmDelete",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dialog,null);
static void Expect(MatchSaveSlotsUI dialog,params int[] expected){var actual=(List<int>)typeof(MatchSaveSlotsUI).GetField("slots",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(dialog);
if(!actual.SequenceEqual(expected))throw new Exception("Expected ["+string.Join(",",expected)+"], got ["+string.Join(",",actual)+"]");}
static int Main(){string root=Path.Combine(Path.GetTempPath(),"save-dialog-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try {var dialog=Dialog(root,true,7);int events=0;dialog.SlotsChanged+=()=>events++;Confirm(dialog);Expect(dialog,0,1,2,3,4,5,6,8);
if(events!=1)throw new Exception("Wrong delete notification count");Console.WriteLine("PASS delete one pending position retains other pending positions");
File.WriteAllText(Path.Combine(root,"slot-2.json"),"save");dialog=Dialog(root,true,2);Confirm(dialog);Expect(dialog,0,1,2,3,4,5,6,7,8);
if(File.Exists(Path.Combine(root,"slot-2.json")))throw new Exception("Primary was not deleted");Console.WriteLine("PASS deleting a base save keeps its empty position and other new positions");
File.WriteAllText(Path.Combine(root,"slot-8.json"),"save");dialog=Dialog(root,false,8);Confirm(dialog);Expect(dialog);Console.WriteLine("PASS load mode removes deleted position without retaining empty placeholders");
File.WriteAllText(Path.Combine(root,"slot-7.json"),"save");Directory.CreateDirectory(Path.Combine(root,"slot-7.json.bak"));
dialog=Dialog(root,true,7);events=0;dialog.SlotsChanged+=()=>events++;Confirm(dialog);Expect(dialog,0,1,2,3,4,5,6,7,8);
if(!File.Exists(Path.Combine(root,"slot-7.json"))||events!=0)throw new Exception("Failed deletion changed the primary or published success");Console.WriteLine("PASS failed deletion retains all positions and does not publish success");
if(Environment.OSVersion.Platform==PlatformID.Unix){
dialog=Dialog(root,true,7);events=0;dialog.SlotsChanged+=()=>events++;
if(chmod(root,0)!=0)throw new Exception("Cannot prepare permission fixture");
try{bool denied=false;try{Directory.GetFiles(root);}catch(UnauthorizedAccessException){denied=true;}
if(denied){Confirm(dialog);Expect(dialog,0,1,2,3,4,5,6,7,8);if(events!=0)throw new Exception("Published success despite denied directory");Console.WriteLine("PASS failed directory refresh retains unsaved positions");}
else Console.WriteLine("SKIP directory permissions bypassed by this process");}
finally{chmod(root,448);}}
return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}finally{Directory.Delete(root,true);}}}
''')
output = work/'DialogCheck.dll'
args = ['-nologo','-target:exe','-langversion:9','-define:UNITY_EDITOR;UNITY_6000_0_OR_NEWER','-nowarn:0436,0618,0649,0414,0169,0219,0162,0168,8632','-out:'+str(output)]
args += ['-r:'+str(p) for p in [*refs.glob('*.dll'),*engine.glob('*.dll'),ui]]
args += [str(p) for p in source.rglob('*.cs')]+[str(work/'EngineStubs.cs'),str(program)]
rsp = work/'compile.rsp';rsp.write_text('\n'.join('"'+x+'"' for x in args))
subprocess.run([str(dotnet),str(sdk/'Roslyn/bincore/csc.dll'),'@'+str(rsp)],check=True)
for p in [*engine.glob('*.dll'),ui]:shutil.copy2(p,work/p.name)
(work/'DialogCheck.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
# Normalize only incompatible placeholder metadata in disposable reference DLL copies.
root=Path(__file__).resolve().parents[2]
adapter_args=['-nologo','-target:exe','-out:'+str(work/'Adapter.dll'),*['-r:'+str(p) for p in refs.glob('*.dll')],str(root/'Tools/save-preparation-verify/ai/ReferenceAdapter.cs')]
adapter_rsp=work/'adapter.rsp';adapter_rsp.write_text('\n'.join('"'+x+'"' for x in adapter_args))
subprocess.run([str(dotnet),str(sdk/'Roslyn/bincore/csc.dll'),'@'+str(adapter_rsp)],check=True)
(work/'Adapter.runtimeconfig.json').write_text((work/'DialogCheck.runtimeconfig.json').read_text())
subprocess.run([str(dotnet),str(work/'Adapter.dll'),*[str(p) for p in work.glob('Unity*.dll')]],check=True)
subprocess.run([str(dotnet),str(output)],check=True)
