#!/usr/bin/env python3
"""Execute Continue destination policy from the production assembly built by the binding gate."""
from pathlib import Path
import subprocess,shutil,json,os,tempfile
dotnet=Path(os.environ['DOTNET']).resolve()
sdk=sorted((dotnet.parent/'sdk').glob('8.*'))[-1]
ref=sorted((dotnet.parent/'packs/Microsoft.NETCore.App.Ref').glob('8.*'))[-1]/'ref/net8.0'
cache=Path(os.environ['NUGET_PACKAGES']); engine=cache/'unityengine.modules/2021.3.33/lib/netstandard2.0'
ui=next((cache/'unity3d.unityengine.ui/2020.3.21').rglob('UnityEngine.UI.dll'))
work=Path(tempfile.mkdtemp(prefix='save-continue-check-'))
source=work/'Program.cs';source.write_text('''using System;using System.Reflection;using Game.UI;
class Program {static int Main(){var method=typeof(MainMenuController).GetMethod("ContinueAction",BindingFlags.Static|BindingFlags.NonPublic);int[] expected={0,1,2,2};int index=0;foreach(bool matches in new[]{false,true})foreach(bool campaign in new[]{false,true}){int got=(int)method.Invoke(null,new object[]{matches,campaign});if(got!=expected[index++])throw new Exception("Wrong Continue destination");Console.WriteLine("PASS matches="+matches+" campaign="+campaign+" => "+got);}return 0;}}''')
assembly=Path(os.environ['SAVE_VERIFY_WORK'])/'current.dll';refs=[*ref.glob('*.dll'),*engine.glob('*.dll'),ui,assembly]
args=['-nologo','-target:exe','-out:'+str(work/'ContinueCheck.dll'),*['-r:'+str(p) for p in refs],str(source)]
rsp=work/'compile.rsp';rsp.write_text('\n'.join('"'+x+'"' for x in args));subprocess.run([str(dotnet),str(sdk/'Roslyn/bincore/csc.dll'),'@'+str(rsp)],check=True)
for p in [*engine.glob('*.dll'),ui,assembly]:shutil.copy2(p,work/p.name)
(work/'ContinueCheck.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
subprocess.run([str(dotnet),str(work/'ContinueCheck.dll')],check=True)
