#!/bin/bash
# Publish a LunarLink release:  tools/release.sh 0.2.0
# Needs a clean main that matches GitHub, that version in src/LunarLink/Server.cs's [BepInPlugin], and a "## 0.2.0"
# section in CHANGELOG.md (rename "## Unreleased" to it). Builds everything stamped "0.2.0 (<time>)", zips it in the game's folder layout
# (BepInEx/plugins/LunarLink/ and BepInEx/hot/) and publishes the GitHub release v0.2.0 with the zip, its changelog
# section as the notes. It builds here because it compiles against the game's own DLLs, which GitHub doesn't have.
#   tools/release.sh 0.2.0 --dry-run   build and zip into dist/ only: no checks of git, no tag, nothing published
set -e
cd "$(dirname "$0")/.."
VER="$1"; TAG="v$VER"; DRY=; [ "$2" = "--dry-run" ] && DRY=1
[[ "$VER" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "usage: tools/release.sh <x.y.z>"; exit 1; }
if [ -z "$DRY" ]; then
[ -z "$(git status --porcelain)" ] || { echo "commit your changes first"; exit 1; }
[ "$(git rev-parse --abbrev-ref HEAD)" = main ] || { echo "release from main"; exit 1; }
git fetch -q origin main
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || { echo "main differs from GitHub's: push or pull first"; exit 1; }
! git rev-parse -q --verify "refs/tags/$TAG" >/dev/null || { echo "$TAG already exists"; exit 1; }
gh release view "$TAG" >/dev/null 2>&1 && { echo "release $TAG already exists on GitHub"; exit 1; }
fi
grep -q "\[BepInPlugin(\"lunarlink\", \"LunarLink\", \"$VER\")\]" src/LunarLink/Server.cs \
    || { echo "set the version in src/LunarLink/Server.cs's [BepInPlugin] to $VER first"; exit 1; }
NOTES=$(awk -v v="## $VER" '$0 == v { on = 1; next } /^## / { if (on) exit } on' CHANGELOG.md)
[ -n "$(echo "$NOTES" | tr -d '[:space:]')" ] || { echo "CHANGELOG.md needs a \"## $VER\" section"; exit 1; }

export STAMP="$VER ($(date '+%Y-%m-%d %H:%M:%S'))"
./build.sh

OUT=$(mktemp -d); trap 'rm -rf "$OUT"' EXIT
P="$OUT/BepInEx/plugins/LunarLink"; H="$OUT/BepInEx/hot"; mkdir -p "$P" "$H"
cp src/LunarLink/bin/Release/net6.0/LunarLink.dll "$P/"
echo "$STAMP" > "$P/installed-version"
cp src/LunarLink/bin/Release/net6.0/manifest.json "$P/manifest.json"   # the updater compares its coreHash
cp LICENSE "$P/LICENSE.txt"
for d in src/LunarLink.*/; do m=$(basename "$d"); cp "$d/bin/Release/net6.0/$m.dll" "$H/"; done
ZIP="$OUT/LunarLink-$VER.zip"
(cd "$OUT" && python3 -m zipfile -c "$ZIP" BepInEx)
if [ -n "$DRY" ]; then mkdir -p dist; cp "$ZIP" dist/; echo "dry run: dist/$(basename "$ZIP")"; python3 -m zipfile -l "dist/$(basename "$ZIP")"; exit 0; fi

gh release create "$TAG" "$ZIP" --target main --title "LunarLink $VER" --notes "$NOTES

### Install

Close the game, unzip into the game's folder (the one that holds \`BepInEx/\`) and start it. Needs only BepInEx 6 (IL2CPP);
MoonFramework is optional. Updating: the same, over the old files; modules you turned off stay off."
echo "released $TAG: $(gh release view "$TAG" --json url -q .url)"
