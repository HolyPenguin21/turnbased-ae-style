#!/usr/bin/env python3
"""Run the existing terrain NUnit tests with managed stubs and a local .NET SDK.

Bypasses dotnet CLI startup/restore on hosts where Process.StartTime is unavailable.
This is not a Unity compilation or engine test run. Supply an existing NUnit 3.14 DLL.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET_HOST_PATH") or shutil.which("dotnet"))
    parser.add_argument("--nunit", default=os.environ.get("NUNIT_DLL"))
    parser.add_argument("--output", default=os.environ.get("TERRAIN_VERIFY_WORK"))
    parser.add_argument("fixture", nargs="?", help="Optional fixture-name filter")
    args = parser.parse_args()
    tool_dir = Path(__file__).resolve().parent
    repo = tool_dir.parents[1]
    if not args.dotnet or not args.nunit:
        parser.error("Supply --dotnet and --nunit (or DOTNET_HOST_PATH and NUNIT_DLL).")
    dotnet = Path(args.dotnet).resolve()
    nunit = Path(args.nunit).resolve()
    if not dotnet.is_file() or not nunit.is_file():
        parser.error("The .NET host or NUnit DLL does not exist.")
    sdk_root = dotnet.parent
    csc = sorted((sdk_root / "sdk").glob("*/Roslyn/bincore/csc.dll"))[-1]
    refs = sorted((sdk_root / "packs/Microsoft.NETCore.App.Ref").glob("8.*/ref/net8.0"))[-1]
    output = Path(args.output).resolve() if args.output else repo.parent / "terrain-verify-work"
    output.mkdir(parents=True, exist_ok=True)
    assembly = output / "TerrainChecks.dll"
    compiler_args = ["-nologo", "-target:exe", "-langversion:9", "-out:" + str(assembly),
                     "-define:UNITY_INCLUDE_TESTS,TERRAIN_MANAGED_HARNESS"]
    compiler_args.extend("-r:" + str(p) for p in refs.glob("*.dll"))
    compiler_args.append("-r:" + str(nunit))
    for element in ET.parse(tool_dir / "TerrainVerify.csproj").findall(".//Compile"):
        compiler_args.extend(str((tool_dir / item).resolve()) for item in element.attrib["Include"].split(";"))
    response = output / "compile.rsp"
    response.write_text("\n".join('"' + value + '"' for value in compiler_args), encoding="utf-8")
    result = subprocess.run([str(dotnet), str(csc), "@" + str(response)], check=False)
    if result.returncode:
        return result.returncode
    shutil.copy2(nunit, output / nunit.name)
    (output / "TerrainChecks.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
        "tfm": "net8.0", "framework": {"name": "Microsoft.NETCore.App", "version": "8.0.0"}}}), encoding="utf-8")
    command = [str(dotnet), str(assembly)]
    if args.fixture:
        command.append(args.fixture)
    return subprocess.run(command, check=False).returncode


if __name__ == "__main__":
    sys.exit(main())
