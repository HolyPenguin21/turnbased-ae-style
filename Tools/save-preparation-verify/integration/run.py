#!/usr/bin/env python3
"""Run the real in-memory staged logical restore chain; no Unity native scene or save codec."""
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
    "Units/UnitData.cs", "Units/UnitData.Restore.cs", "Units/SerializableNullableInt.cs", "Cards/CardData.cs",
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
SOURCES += [
    "Core/GameSession.cs", "Core/GameSession.Restore.cs", "Setup/IMatchRestoreSource.cs",
    "Setup/MatchRestoreCoordinator.cs", "Setup/MatchIdentityState.cs", "Setup/MatchStateInventory.cs",
    "Setup/MatchSaveEligibility.cs", "Turns/GameTurnController.Persistence.cs", "Turns/GameTurnState.cs",
    "Map/PlayerRootRegistry.cs", "Map/HexMapLayout.cs", "Map/HexSelectionController.Persistence.cs",
    "Map/MapResourceDisplay.cs", "Map/MapEventDisplay.cs", "Cards/PlayerHandData.cs", "Cards/CardState.cs",
    "Cards/DeckDraw.cs", "Cards/MatchLoadout.cs", "Progression/CollectionProfile.cs", "Ai/AiHandData.cs",
    "Cards/TacticHand.cs", "Players/Faction.cs", "Cards/DeckRules.cs", "Core/AtomicJsonFile.cs",
    "Progression/CollectionProfileStore.cs", "Progression/CollectionService.cs",
]


def latest(paths):
    return sorted(paths, key=lambda p: tuple(int(x) for x in re.findall(r"\d+", p.name.split("-")[0])))[-1]


def main():
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
    files += [HERE / "PortableAdapters.cs", HERE / "Program.cs", HERE.parents[1] / "campaign-verify/PortableJson.cs"]
    # This partial is the actual scene-owner restore/failure transaction, also called by Start.
    helper = REPO / "Assets/Scripts/Setup/CitadelSetupController.Restore.cs"
    if helper.exists(): files.append(helper)
    with tempfile.TemporaryDirectory(prefix="restore-integration-verify-") as temporary:
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
