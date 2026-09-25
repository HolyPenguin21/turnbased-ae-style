#!/bin/bash
# One-time: .NET 8 SDK (compile check) + Mono (runs the net472 test exe; CoreCLR cannot load the
# real UnityEngine assemblies). Both come from the Ubuntu archive (dot.net downloads may be blocked).
set -e
export DEBIAN_FRONTEND=noninteractive
if ! command -v dotnet >/dev/null || ! command -v mono >/dev/null; then
  apt-get update -qq || true
  command -v dotnet >/dev/null || apt-get install -y -qq dotnet-sdk-8.0
  command -v mono >/dev/null || apt-get install -y -qq mono-runtime mono-devel
fi
dotnet --version && mono --version | head -1
