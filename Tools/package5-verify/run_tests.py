#!/usr/bin/env python3
"""Differential NUnit checks against an explicitly adapted integration runtime. Not native Unity."""
import argparse,io,json,os,shutil,subprocess,tarfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
p=argparse.ArgumentParser();p.add_argument('--runtime',required=True);p.add_argument('--work',required=True);p.add_argument('--base');p.add_argument('--where',default='cat != UnityNative && cat != Extended');a=p.parse_args()
runtime=Path(a.runtime);work=Path(a.work);work.mkdir(parents=True,exist_ok=True)
dotnet=Path(os.environ['DOTNET']);sdk=sorted((dotnet.parent/'sdk').glob('8.*'))[-1]
refs=list((sorted((dotnet.parent/'packs/Microsoft.NETCore.App.Ref').glob('8.*'))[-1]/'ref/net8.0').glob('*.dll'));netrefs=refs[:]
cache=Path(os.environ['NUGET_PACKAGES']);engine=cache/'unityengine.modules/2021.3.33/lib/netstandard2.0';ui=next((cache/'unity3d.unityengine.ui/2020.3.21').rglob('UnityEngine.UI.dll'))
nunit=cache/'nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll';lite=cache/'nunitlite/3.14.0/lib/netstandard2.0/nunitlite.dll';refs += [*engine.glob('*.dll'),ui,nunit,lite]
root=ROOT
if a.base:
 root=work/'base';root.mkdir(exist_ok=True)
 data=subprocess.run(['git','archive',a.base,'Assets/Editor/Tests','Assets/Editor/Tools/MatchSaveContentFingerprint.cs'],cwd=ROOT,capture_output=True,check=True).stdout
 with tarfile.open(fileobj=io.BytesIO(data)) as z:z.extractall(root,filter='data')
(work/'IntegrationNative.cs').write_text((ROOT/'Tools/match-save-verify/integration-program.cs').read_text().split('internal static class IntegrationProgram')[0])
(work/'Runner.cs').write_text('using System;class Runner{static int Main(string[] args)=>new NUnitLite.AutoRun().Execute(args);}namespace UnityEngine.TestTools{[AttributeUsage(AttributeTargets.Method)]public sealed class UnityTestAttribute:Attribute{}}')
tests=[f for folder in ['Core','Map','Combat','Cards','Turns'] for f in (root/'Assets/Editor/Tests'/folder).glob('*.cs') if f.name!='DeckCollectionTests.cs']
files=[*runtime.joinpath('runtime').rglob('*.cs'),runtime/'EngineStubs.cs',runtime/'Math.cs',work/'IntegrationNative.cs',work/'Runner.cs',ROOT/'Tools/save-preparation-verify/NativeEditorAdapters.cs',root/'Assets/Editor/Tools/MatchSaveContentFingerprint.cs',*tests]
def compile(name,files,refs,extra=()):
 args=['-nologo','-target:exe','-langversion:9','-unsafe','-define:UNITY_INCLUDE_TESTS;UNITY_EDITOR;UNITY_6000_0_OR_NEWER','-nowarn:0436,0618,0649,0414,0169,0219,0162,0168,8632','-out:'+str(work/(name+'.dll')),*['-r:'+str(x) for x in refs],*extra,*files]
 rsp=work/(name+'.rsp');rsp.write_text('\n'.join('"'+str(x)+'"' for x in args));subprocess.run([str(dotnet),str(sdk/'Roslyn/bincore/csc.dll'),'@'+str(rsp)],check=True)
 (work/(name+'.runtimeconfig.json')).write_text((runtime/'Integration.runtimeconfig.json').read_text())
compile('Tests',files,refs)
for f in [*runtime.glob('Unity*.dll'),nunit,lite]:shutil.copy2(f,work/f.name)
compile('ReferenceAdapter',[ROOT/'Tools/save-preparation-verify/ai/ReferenceAdapter.cs'],netrefs)
subprocess.run([str(dotnet),str(work/'ReferenceAdapter.dll'),*[str(x) for x in work.glob('Unity*.dll')]],check=True)
raise SystemExit(subprocess.run([str(dotnet),str(work/'Tests.dll'),'--where',a.where,'--workers=0','--noheader','--labels=Off','--result='+str(work/'results.xml')],cwd=ROOT).returncode)
