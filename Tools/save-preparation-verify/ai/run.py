#!/usr/bin/env python3
"""Run the actual AI preparation NUnit fixture against complete runtime source and cached Unity refs.
Compatibility adapters exist only in the temporary copy. This does not execute Unity's native engine.
"""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
executable = os.environ.get("DOTNET") or os.environ.get("DOTNET_BIN") or shutil.which("dotnet")
if not executable:
    raise SystemExit("Set DOTNET to the .NET 8 SDK executable.")
dotnet = Path(executable).resolve()
sdk = sorted((dotnet.parent / "sdk").glob("8.*"))[-1]
refpack = sorted((dotnet.parent / "packs/Microsoft.NETCore.App.Ref").glob("8.*"))[-1]
packages = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
engine = packages / "unityengine.modules/2021.3.33/lib/netstandard2.0"
ui = list((packages / "unity3d.unityengine.ui/2020.3.21").rglob("UnityEngine.UI.dll"))
nunit = packages / "nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll"
if not engine.is_dir() or not ui or not nunit.is_file():
    raise SystemExit("Restore cached UnityEngine.Modules 2021.3.33, Unity UI and NUnit 3.14.0; set NUGET_PACKAGES.")
with tempfile.TemporaryDirectory(prefix="ai-state-verify-") as temporary:
    output = Path(temporary)
    source = output / "runtime"
    shutil.copytree(REPO / "Assets/Scripts", source)
    for file in source.rglob("*.cs"):
        text = file.read_text()
        text = text.replace("FindObjectsInactive.Include, FindObjectsSortMode.None", "FindObjectsSortMode.None")
        text = re.sub(r"(FindObjectsByType<[^>]+>\()FindObjectsInactive\.(?:Include|Exclude)(\))", r"\1FindObjectsSortMode.None\2", text)
        file.write_text(text)
    # The fixture restores memory with a literal null map. Unity's overloaded Object null
    # comparison needs native engine support; the temporary copy uses CLR null identity here.
    map_memory = source / "Ai/AiMapMemory.cs"
    map_memory.write_text(map_memory.read_text().replace(
        "if (map != null)\n                _map = map;",
        "if (!object.ReferenceEquals(map, null))\n                _map = map;"))
    stub = (REPO / "Tools/ai-verify/EngineStubs.cs").read_text()
    stub = stub.replace("    public enum TextAlignmentOptions", "    public enum VerticalAlignmentOptions { Top,Middle,Bottom,Baseline,Geometry,Capline }\n    public enum TextAlignmentOptions")
    stub = stub.replace("public TextAlignmentOptions alignment { get; set; }", "public TextAlignmentOptions alignment { get; set; } public VerticalAlignmentOptions verticalAlignment {get;set;}")
    stub = stub.replace("public TMP_Text captionText { get; set; }", "public TMP_Text captionText { get; set; } public TMP_Text itemText {get;set;} public UnityEngine.RectTransform template {get;set;}")
    (output / "EngineStubs.cs").write_text(stub)
    math = (REPO / "Tools/ai-verify/TestRunStubs.cs").read_text()
    math = math.replace("    public const float Epsilon", "    public static float Log10(float value) => (float)System.Math.Log10(value);\n    public const float Epsilon")
    (output / "ManagedMath.cs").write_text(math)
    files = [*source.rglob("*.cs"), output / "EngineStubs.cs", output / "ManagedMath.cs",
             REPO / "Assets/Editor/Tests/AI/AiPersistencePreparationTests.cs",
             REPO / "Assets/Editor/Tests/AI/AiPersistentGraphRoundTripTests.cs",
             REPO / "Assets/Editor/Tests/AI/MatchAiJsonCodecTests.cs",
             REPO / "Assets/Editor/Tests/AI/MatchAiAggregateJsonTests.cs",
             REPO / "Assets/Editor/Tests/Core/MatchRestorePreparationTests.cs", HERE / "Program.cs"]
    refs = [*(refpack / "ref/net8.0").glob("*.dll"), *engine.glob("*.dll"), ui[0], nunit]
    args = ["-nologo", "-target:exe", "-langversion:9", "-unsafe", "-define:UNITY_INCLUDE_TESTS;UNITY_EDITOR;UNITY_6000_0_OR_NEWER",
            "-nowarn:0436,0618,0649,0414,0169,0219,0162,0168,8632", "-out:" + str(output / "AiCheck.dll")]
    args += ["-r:" + str(path) for path in refs] + [str(file) for file in files]
    response = output / "compile.rsp"
    response.write_text("\n".join('"' + argument + '"' for argument in args))
    subprocess.run([str(dotnet), str(sdk / "Roslyn/bincore/csc.dll"), "@" + str(response)], check=True)
    for reference in [*engine.glob("*.dll"), ui[0], nunit]:
        shutil.copy2(reference, output / reference.name)
    runtime = json.dumps({"runtimeOptions": {"tfm": "net8.0", "framework": {"name": "Microsoft.NETCore.App", "version": refpack.name}}})
    (output / "AiCheck.runtimeconfig.json").write_text(runtime)
    adapter_args = ["-nologo", "-target:exe", "-out:" + str(output / "ReferenceAdapter.dll")]
    adapter_args += ["-r:" + str(path) for path in (refpack / "ref/net8.0").glob("*.dll")]
    adapter_args += [str(HERE / "ReferenceAdapter.cs")]
    adapter_response = output / "adapter.rsp"
    adapter_response.write_text("\n".join('"' + argument + '"' for argument in adapter_args))
    subprocess.run([str(dotnet), str(sdk / "Roslyn/bincore/csc.dll"), "@" + str(adapter_response)], check=True)
    (output / "ReferenceAdapter.runtimeconfig.json").write_text(runtime)
    subprocess.run([str(dotnet), str(output / "ReferenceAdapter.dll"), *[str(output / path.name) for path in engine.glob("*.dll")]], check=True)
    subprocess.run([str(dotnet), str(output / "AiCheck.dll")], check=True)
