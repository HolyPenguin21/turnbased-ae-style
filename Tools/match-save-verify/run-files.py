#!/usr/bin/env python3
"""Execute production file storage/strict JSON checks; no Unity substitutes needed."""
import json, os, shutil, subprocess, tempfile
from pathlib import Path
repo = Path(__file__).resolve().parents[2]
dotnet = Path(os.environ['DOTNET']).resolve()
sdk = sorted((dotnet.parent/'sdk').glob('8.*'))[-1]
refs = sorted((dotnet.parent/'packs/Microsoft.NETCore.App.Ref').glob('8.*'))[-1]/'ref/net8.0'
nunit = Path(os.environ['NUGET_PACKAGES'])/'nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll'
work = Path(os.environ.get('SAVE_FILE_VERIFY_WORK', tempfile.mkdtemp(prefix='match-file-check-')))
work.mkdir(parents=True, exist_ok=True)
(work/'Program.cs').write_text('''using System; using System.Linq; using System.Reflection; using NUnit.Framework;
class Program { static int Main() { Game.UI.CardHandUI.VerifyCaptureGate(); int checks=0,failed=0; var type=typeof(Game.Tests.Core.MatchSaveFileTests);
foreach(var method in type.GetMethods()) { var cases=method.GetCustomAttributes<TestCaseAttribute>().ToArray();
if(!method.IsDefined(typeof(TestAttribute)) && cases.Length==0) continue;
foreach(var args in cases.Length==0 ? new[]{Array.Empty<object>()} : cases.Select(x=>x.Arguments)) {
 var fixture=Activator.CreateInstance(type); type.GetMethod("SetUp").Invoke(fixture,null);
 try{method.Invoke(fixture,args);checks++; Console.WriteLine("PASS "+method.Name);}
 catch(Exception ex){failed++;Console.WriteLine("FAIL "+method.Name+": "+(ex.InnerException??ex));}
 finally{type.GetMethod("TearDown").Invoke(fixture,null);}
}} Console.WriteLine($"{checks} passed, {failed} failed");return failed==0?0:1; }}''')
sources = [repo/p for p in [
 'Assets/Scripts/Core/AtomicJsonFile.cs', 'Assets/Scripts/Saves/MatchSaveStore.cs',
 'Assets/Scripts/Saves/MatchSaveJson.cs', 'Assets/Editor/Tests/Core/MatchSaveFileTests.cs',
 'Assets/Scripts/UI/Cards/CardHandUI.Persistence.cs','Tools/match-save-verify/CardOperationCheck.cs']]
out = work/'FileCheck.dll'
args=['-nologo','-target:exe','-langversion:9','-define:UNITY_INCLUDE_TESTS','-out:'+str(out)]
args += ['-r:'+str(p) for p in refs.glob('*.dll')]+['-r:'+str(nunit)]
args += [str(p) for p in sources]+[str(work/'Program.cs')]
rsp=work/'compile.rsp';rsp.write_text('\n'.join('"'+x+'"' for x in args))
subprocess.run([str(dotnet),str(sdk/'Roslyn/bincore/csc.dll'),'@'+str(rsp)],check=True)
shutil.copy2(nunit,work/nunit.name)
(work/'FileCheck.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
subprocess.run([str(dotnet),str(out)],check=True)
