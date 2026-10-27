#!/usr/bin/env python3
"""Compile the real hand model/test sources directly with the .NET 8 SDK's csc."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
SOURCES = [
    "Assets/Scripts/Cards/CardData.cs",
    "Assets/Scripts/Cards/PlayerHandData.cs",
    "Assets/Scripts/Cards/CardState.cs",
    "Assets/Scripts/Cards/DeckDraw.cs",
    "Assets/Scripts/Cards/MatchLoadout.cs",
    "Assets/Scripts/Progression/CollectionProfile.cs",
    "Assets/Scripts/Ai/AiHandData.cs",
    "Assets/Editor/Tests/Cards/PlayerHandStateTests.cs",
    "Assets/Editor/Tests/AI/AiHandCapacityParityTests.cs",
]


def latest(paths):
    return sorted(paths, key=lambda p: tuple(int(n) for n in p.name.split(".") if n.isdigit()))[-1]


def main():
    executable = os.environ.get("DOTNET") or shutil.which("dotnet")
    if not executable:
        raise RuntimeError("Set DOTNET to the .NET 8 SDK's dotnet executable, or add it to PATH.")
    dotnet = Path(executable).resolve()
    root = Path(os.environ.get("DOTNET_ROOT", dotnet.parent))
    sdks = [p for p in (root / "sdk").glob("8.*") if (p / "Roslyn/bincore/csc.dll").is_file()]
    packs = [p for p in (root / "packs/Microsoft.NETCore.App.Ref").glob("8.*") if (p / "ref/net8.0").is_dir()]
    if not sdks or not packs:
        raise RuntimeError("The .NET 8 SDK and Microsoft.NETCore.App.Ref 8.x pack are required.")
    package_root = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
    nunit = Path(os.environ.get("NUNIT_DLL", package_root / "nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll"))
    if not nunit.is_file():
        raise RuntimeError("NUnit 3.14.0 is required. Restore it in the NuGet cache or set NUNIT_DLL to nunit.framework.dll.")
    workspace = os.environ.get("SAVE_HAND_VERIFY_WORK")
    out = Path(workspace).resolve() if workspace else Path(tempfile.mkdtemp(prefix="save-hand-verify-"))
    out.mkdir(parents=True, exist_ok=True)
    assembly = out / "HandCheck.dll"
    arguments = ["-nologo", "-target:exe", "-langversion:9.0", "-define:UNITY_INCLUDE_TESTS", "-out:" + str(assembly)]
    arguments += ["-r:" + str(p) for p in sorted((latest(packs) / "ref/net8.0").glob("*.dll"))]
    arguments += ["-r:" + str(nunit), str(HERE / "EngineCatalogAdapters.cs"), str(HERE / "Program.cs")]
    arguments += [str(REPO / source) for source in SOURCES]
    response = out / "compile.rsp"
    response.write_text("\n".join('"' + arg + '"' for arg in arguments) + "\n")
    # Avoid dotnet build/MSBuild: this path invokes Roslyn directly and needs no project restore.
    subprocess.run([str(dotnet), str(latest(sdks) / "Roslyn/bincore/csc.dll"), "@" + str(response)], check=True)
    shutil.copyfile(nunit, out / "nunit.framework.dll")
    (out / "HandCheck.runtimeconfig.json").write_text(json.dumps({
        "runtimeOptions": {"tfm": "net8.0", "framework": {"name": "Microsoft.NETCore.App", "version": "8.0.0"}}
    }))
    print("Running production hand-model sources with isolated Tools adapters.", flush=True)
    subprocess.run([str(dotnet), str(assembly)], check=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (RuntimeError, subprocess.CalledProcessError) as error:
        print("Hand verification failed: " + str(error), file=sys.stderr)
        sys.exit(1)
