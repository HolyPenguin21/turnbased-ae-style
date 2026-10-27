#!/usr/bin/env python3
"""Compile actual world-state owners with portable managed adapters, then run focused NUnit checks."""
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
    "Map/ArmyData.cs", "Map/ArmyData.Transport.cs", "Map/ArmyData.Restore.cs",
    "Units/UnitData.cs", "Units/UnitData.Restore.cs", "Cards/CardData.cs",
    "Map/ArmyRegistry.cs", "Map/BuildingRegistry.cs", "Map/WorldPreparationState.cs",
    "Map/VisionSystem.cs", "Map/VisionSystem.Restore.cs", "Map/StealthSystem.cs", "Map/StealthSystem.Restore.cs",
    "Map/HumanVisualMemory.cs", "Map/HumanVisualMemory.Restore.cs", "Map/PlayerRoot.cs", "Map/PlayerRoot.Restore.cs",
    "Players/PlayerSetupData.cs", "Aviation/AntiAirState.cs", "Aviation/AntiAirState.Restore.cs",
    "Combat/DelayedBattleRegistry.cs", "Combat/DelayedBattleRegistry.Restore.cs", "Combat/PendingBattle.cs",
    "HexGrid/HexCoord.cs", "HexGrid/HexGridMath.cs", "Cards/UnitTypeTag.cs", "Units/BattleRow.cs",
    "Economy/ResourceType.cs", "Economy/ResourceYields.cs", "Cards/EventCatalog.cs", "Setup/MatchRestoreScope.cs",
    "Map/HexResourceBonusRegistry.cs", "Map/HexResourceBonusRegistry.Restore.cs",
    "Map/HexEventRegistry.cs", "Map/HexEventRegistry.Restore.cs",
]
TESTS = ["Core/RuntimeIdentityRestoreTests.cs", "Core/WorldStatePreparationTests.cs", "Core/HexRegistryStatePreparationTests.cs"]


def latest(paths):
    return sorted(paths, key=lambda p: tuple(int(x) for x in re.findall(r"\d+", p.name.split("-")[0])))[-1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--original-17", action="store_true", help="Run only the original world/identity checks.")
    options = parser.parse_args()
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
    nuget = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
    nunit = Path(os.environ.get("NUNIT_FRAMEWORK_DLL", str(nuget / "nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll")))
    if not nunit.is_file():
        raise SystemExit("NUnit 3.14.0 is required. Restore the nunit package, or set NUNIT_FRAMEWORK_DLL; see README.md.")
    sources = [source for source in SOURCES if not options.original_17 or source not in (
        "Map/HexEventRegistry.Restore.cs", "Map/HexResourceBonusRegistry.Restore.cs")]
    tests = TESTS[:2] if options.original_17 else TESTS
    files = [REPO / "Assets/Scripts" / source for source in sources]
    files += [REPO / "Assets/Editor/Tests" / source for source in tests]
    files += [HERE / "PortableAdapters.cs", HERE / "Program.cs"]
    with tempfile.TemporaryDirectory(prefix="world-state-verify-") as temporary:
        output = Path(temporary)
        arguments = ["-nologo", "-target:exe", "-out:" + str(output / "checks.dll"), "-define:UNITY_INCLUDE_TESTS", "-langversion:9"]
        arguments += ["-r:" + str(path) for path in refs.glob("*.dll")]
        arguments += ["-r:" + str(nunit)] + [str(path) for path in files]
        response = output / "compile.rsp"
        response.write_text("\n".join('"' + argument + '"' for argument in arguments))
        compiled = subprocess.run([str(dotnet), str(sdk / "Roslyn/bincore/csc.dll"), "@" + str(response)], cwd=REPO)
        if compiled.returncode:
            return compiled.returncode
        shutil.copyfile(nunit, output / "nunit.framework.dll")
        installed = latest([p for p in (runtime / "shared/Microsoft.NETCore.App").iterdir() if p.is_dir()])
        (output / "checks.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {"tfm": refs.name, "framework": {"name": "Microsoft.NETCore.App", "version": installed.name}}}))
        return subprocess.run([str(dotnet), str(output / "checks.dll")], cwd=REPO).returncode


if __name__ == "__main__":
    sys.exit(main())
