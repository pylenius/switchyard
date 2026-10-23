#!/usr/bin/env bash
set -euo pipefail

# Two ways to ship it:
#   ./publish.sh pack        artifacts/nupkg/Switchyard.<version>.nupkg, for `dotnet tool install -g Switchyard`
#   ./publish.sh binaries    framework-dependent builds per platform into artifacts/{rid}/ (~700 KB each;
#                            a self-contained single file is 100+ MB), for repos that commit the tool

cd "$(dirname "$0")"
PROJECT=src/Switchyard/Switchyard.csproj

case "${1:-pack}" in
  pack)
    dotnet pack "$PROJECT" -c Release -o artifacts/nupkg --nologo
    ;;
  binaries)
    for RID in osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64 win-arm64; do
      rm -rf "artifacts/$RID"
      dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained false -p:PackAsTool=false \
        -p:DebugType=none -o "artifacts/$RID" --nologo -v quiet
      # Not needed at runtime: the Web SDK's static web assets manifest and IIS web.config.
      rm -f "artifacts/$RID"/*.staticwebassets.*.json "artifacts/$RID"/web.config
    done
    du -sh artifacts/*/
    ;;
  *)
    echo "Usage: ./publish.sh [pack|binaries]" >&2
    exit 1
    ;;
esac
