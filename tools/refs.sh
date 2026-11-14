#!/bin/bash
# Refresh the private build references after a game or BepInEx update: copies every DLL Directory.Build.props
# references from the game into a checkout of the references repo and pushes it. GitHub Actions builds against those
# (.github/workflows/build.yml); they come from the game, so that repo is private. It only adds and replaces files,
# so the files other mods sharing that repo need are left alone.
#   tools/refs.sh [checkout]      default ../moonsweepers-refs (git clone git@github.com:swheettaos/moonsweepers-refs.git)
# A fork that wants its own CI makes its own private repo the same way and sets REFS_REPO and REFS_DEPLOY_KEY.
set -e
cd "$(dirname "$0")/.."
GAME="${MOON_GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Moon Sweepers}"
R="${1:-../moonsweepers-refs}"
[ -d "$R/.git" ] || { echo "no checkout at $R (clone the references repo there first)"; exit 1; }
grep -o 'GameDir)/[^"]*' Directory.Build.props | sed 's/GameDir)\///' | while read -r f; do
    mkdir -p "$R/$(dirname "$f")"; cp "$GAME/$f" "$R/$f"
done
cd "$R"; git add -A
if git diff --cached --quiet; then echo "the references are already up to date"; exit 0; fi
BEP=$(grep -m1 -o "BepInEx [0-9][^ ]*" "$GAME/BepInEx/LogOutput.log" 2>/dev/null || echo "BepInEx")
git commit -q -m "LunarLink's references refreshed from the game ($(date +%F), $BEP)"
git push -q
echo "pushed: $(git log --oneline -1)"
