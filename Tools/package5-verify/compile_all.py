#!/usr/bin/env python3
"""All-Assets differential legacy-reference compilation; native Unity remains required."""
import io,os,re,subprocess,tarfile,tempfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];dotnet=Path(os.environ['DOTNET']);cache=Path(os.environ['NUGET_PACKAGES']);sdk=sorted((dotnet.parent/'sdk').glob('8.*'))[-1]
refs=[*(sorted((dotnet.parent/'packs/Microsoft.NETCore.App.Ref').glob('8.*'))[-1]/'ref/net8.0').glob('*.dll'),*(cache/'unityengine.modules/2021.3.33/lib/netstandard2.0').glob('*.dll'),next((cache/'unity3d.unityengine.ui/2020.3.21').rglob('UnityEngine.UI.dll')),cache/'nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll']
work=Path(os.environ.get('PACKAGE5_COMPILE_WORK',tempfile.mkdtemp(prefix='package5-compile-')));work.mkdir(exist_ok=True)
base=os.environ.get('PACKAGE5_BASE','b1e80d44f74d3e8bf8b68524811e437cef3cc374');baseline=work/'base';baseline.mkdir(exist_ok=True)
data=subprocess.run(['git','archive',base,'Assets/Scripts','Assets/Editor'],cwd=ROOT,capture_output=True,check=True).stdout
with tarfile.open(fileobj=io.BytesIO(data)) as z:z.extractall(baseline,filter='data')
stub=(ROOT/'Tools/ai-verify/EngineStubs.cs').read_text().replace('    public enum TextAlignmentOptions','    public enum VerticalAlignmentOptions { Top,Middle,Bottom,Baseline,Geometry,Capline }\n    public enum TextAlignmentOptions').replace('public TextAlignmentOptions alignment { get; set; }','public TextAlignmentOptions alignment { get; set; } public VerticalAlignmentOptions verticalAlignment {get;set;}').replace('public TMP_Text captionText { get; set; }','public TMP_Text captionText { get; set; } public TMP_Text itemText {get;set;} public UnityEngine.RectTransform template {get;set;}')
(work/'EngineStubs.cs').write_text(stub);errors={}
for label,tree in [('baseline',baseline),('current',ROOT)]:
 args=['-nologo','-target:library','-unsafe','-langversion:9','-define:UNITY_INCLUDE_TESTS;UNITY_EDITOR;UNITY_2021_3_OR_NEWER;UNITY_6000_0_OR_NEWER','-out:'+str(work/(label+'.dll')),*['-r:'+str(x) for x in refs],*tree.joinpath('Assets').rglob('*.cs'),work/'EngineStubs.cs']
 rsp=work/(label+'.rsp');rsp.write_text('\n'.join('"'+str(x)+'"' for x in args));r=subprocess.run([str(dotnet),str(sdk/'Roslyn/bincore/csc.dll'),'@'+str(rsp)],capture_output=True,text=True)
 (work/(label+'.log')).write_text(r.stdout+r.stderr)
 errors[label]={re.sub(r'\(\d+,\d+\)','',re.sub(r'^.*?Assets/','Assets/',l)) for l in (r.stdout+r.stderr).splitlines() if ': error ' in l}
 if r.returncode and not errors[label]:raise SystemExit(r.stdout+r.stderr)
 print(label+': '+str(len(errors[label]))+' unique legacy-reference diagnostics')
new=errors['current']-errors['baseline'];print(str(len(new))+' new diagnostics')
for l in sorted(new):print(l)
(work/'diagnostics.txt').write_text('\n'.join(sorted(errors['baseline']))+'\nNEW\n'+'\n'.join(sorted(new)))
raise SystemExit(bool(new))
