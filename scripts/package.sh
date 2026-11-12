#!/usr/bin/env bash
# Build a release zip for Unity Mod Manager and Nexus Mods: dist/<Id>-<version>.zip holding one
# <Id> folder with exactly the files the mod needs, its license and changelog. Runs the tests
# first, and builds without the development tools (-p:DevTools=false: the kiosk spike and the
# runtime probe). Builds and tests run under a memory cap.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dv="${DV_INSTALL_DIR:-$HOME/.local/share/Steam/steamapps/common/Derail Valley}"
info="$repo/src/Contracts.Mod/Info.json"
id="$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["Id"])' "$info")"
version="$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["Version"])' "$info")"
out="$repo/src/Contracts.Mod/bin/Release/net48"
zipfile="$repo/dist/$id-$version.zip"
cap=(systemd-run --user --scope -q -p MemoryMax=6G -p MemorySwapMax=0)

echo "Packaging $id $version"
"${cap[@]}" dotnet test "$repo/Contracts.slnx" -c Release -nologo -v quiet -p:DvInstallDir="$dv"
# The Multiplayer part builds the mod with it (its project references it) and lands beside Contracts.dll.
if ! build="$("${cap[@]}" dotnet build "$repo/src/Contracts.Multiplayer/Contracts.Multiplayer.csproj" -c Release -p:DvInstallDir="$dv" -p:DevTools=false --no-incremental -nologo 2>&1)"; then
    grep -E "error" <<<"$build" | sort -u >&2
    exit 1
fi
if grep -q -E "warning CS" <<<"$build"; then
    grep -E "warning CS" <<<"$build" | sort -u >&2
    exit 1
fi
# The development tools must not be in the release build.
if grep -a -q -E "KioskSpike|SpikeDesk" "$out/Contracts.dll" || strings -el "$out/Contracts.dll" | grep -q -E "probe\.txt|spike\.txt"; then
    echo "The release build still holds a development tool" >&2
    exit 1
fi

stage="$repo/dist/stage"
rm -rf "${stage:?}" "$zipfile"
mkdir -p "$stage/$id"
# Never the Multiplayer mod's own API: it ships that, and won't load beside another copy.
if [ -e "$out/MultiplayerAPI.dll" ]; then
    echo "MultiplayerAPI.dll is in the build output; it must not be packaged" >&2
    exit 1
fi
cp "$out/Info.json" "$out/Contracts.dll" "$out/Contracts.Core.dll" "$out/Contracts.Multiplayer.dll" "$stage/$id/"
cp "$repo/LICENSE" "$repo/CHANGELOG.md" "$stage/$id/"
(cd "$stage" && zip -q -X -r "$zipfile" "$id")
rm -rf "${stage:?}"
unzip -l "$zipfile"
sha256sum "$zipfile"
