#!/usr/bin/env python3
"""Compile production map presentation owners and restore coordinator with portable adapters; run regression assertions."""
import argparse
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
SOURCES = [
    "Map/MapEventDisplay.cs", "Map/MapResourceDisplay.cs",
    "Map/HexEventRegistry.cs", "Map/HexEventRegistry.Restore.cs",
    "Map/HexResourceBonusRegistry.cs", "Map/HexResourceBonusRegistry.Restore.cs",
    "Economy/ResourceYields.cs", "Economy/ResourceType.cs", "HexGrid/HexCoord.cs",
    "Setup/MatchRestoreCoordinator.cs", "Setup/MatchRestoreScope.cs",
]


def latest(paths):
    return sorted(paths, key=lambda p: tuple(int(x) for x in re.findall(r"\d+", p.name.split("-")[0])))[-1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    executable = os.environ.get("DOTNET") or shutil.which("dotnet")
    if not executable and os.environ.get("DOTNET_ROOT"):
        executable = str(Path(os.environ["DOTNET_ROOT"]) / "dotnet")
    if not executable or not Path(executable).is_file():
        raise SystemExit("Set DOTNET to a .NET 8+ dotnet executable, or put dotnet on PATH.")
    dotnet = Path(executable).resolve()
    runtime = Path(os.environ.get("DOTNET_ROOT", str(dotnet.parent)))
    sdk = latest([p for p in (runtime / "sdk").iterdir() if (p / "Roslyn/bincore/csc.dll").is_file()])
    refpack = latest([p for p in (runtime / "packs/Microsoft.NETCore.App.Ref").iterdir() if (p / "ref").is_dir()])
    refs = latest([p for p in (refpack / "ref").iterdir() if p.name.startswith("net")])
    files = [REPO / "Assets/Scripts" / source for source in SOURCES]
    files += [HERE / "PortableAdapters.cs", HERE / "Program.cs"]
    with tempfile.TemporaryDirectory(prefix="presentation-verify-") as temporary:
        output = Path(temporary)
        arguments = ["-nologo", "-target:exe", "-out:" + str(output / "checks.dll"), "-langversion:9"]
        arguments += ["-r:" + str(path) for path in refs.glob("*.dll")]
        arguments += [str(path) for path in files]
        response = output / "compile.rsp"
        response.write_text("\n".join('"' + argument + '"' for argument in arguments))
        compiled = subprocess.run([str(dotnet), str(sdk / "Roslyn/bincore/csc.dll"), "@" + str(response)], cwd=REPO)
        if compiled.returncode:
            return compiled.returncode
        installed = latest([p for p in (runtime / "shared/Microsoft.NETCore.App").iterdir() if p.is_dir()])
        (output / "checks.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {"tfm": refs.name, "framework": {"name": "Microsoft.NETCore.App", "version": installed.name}}}))
        return subprocess.run([str(dotnet), str(output / "checks.dll")], cwd=REPO).returncode


if __name__ == "__main__":
    sys.exit(main())
