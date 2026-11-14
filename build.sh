#!/bin/bash
# Build LunarLink: the core (src/LunarLink) and its hot modules (src/LunarLink.<Module>), stamped with the build
# time (/status reports the core's; /hot/list each module's).
#   ./build.sh                 build everything
#   ./build.sh --install       the core into BepInEx/plugins/LunarLink/ (writes installed-version) and the
#                              modules into BepInEx/hot/ (the core turns a new module on at start, unless turned off before);
#                              with the game running, staged for the next start the way the in-game updater does it
#   ./build.sh --hot <Name>    game running: build one module (World, Build...) or a sample (samples/<Name>), copy it
#                              into BepInEx/hot/ and reload it. The core itself only loads at game start.
set -e
cd "$(dirname "$0")"
GAME="${MOON_GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Moon Sweepers}"
HOT="$GAME/BepInEx/hot"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET=dotnet   # the SDK in ~/.dotnet, or on the PATH (CI)
V="${STAMP:-$(date "+%Y-%m-%d %H:%M:%S")}"          # tools/release.sh sets STAMP to "<version> (<time>)"
MODULES=$(cd src && ls -d LunarLink.*/ | tr -d / | sed 's/^LunarLink\.//')

build() {   # build <project dir>
    local out; out=$("$DOTNET" build "$1" -c Release "-p:InformationalVersion=$V" "-p:GameDir=$GAME" 2>&1) || true   # reported here, not by set -e
    echo "$out" | grep -q "Build succeeded" && ! echo "$out" | grep -q " error " && return 0
    echo "$out" | grep -E " error " | sed 's/ \[.*//' | sort -u; echo "build FAILED: $1"; exit 1
}
copy_hot() {   # copy_hot <project dir> <assembly name>
    mkdir -p "$HOT"
    for f in dll pdb; do cp "$1/bin/Release/net6.0/$2.$f" "$HOT/$2.$f.new" && mv -f "$HOT/$2.$f.new" "$HOT/$2.$f"; done   # never written over a loaded file
}
running() { curl -s -m 2 127.0.0.1:8765/status >/dev/null 2>&1; }

if [ "$1" = "--hot" ]; then
    N="$2"
    if [ -n "$N" ] && [ -d "src/LunarLink.$N" ]; then P="src/LunarLink.$N"; A="LunarLink.$N"
    elif [ -n "$N" ] && [ -d "samples/$N" ]; then P="samples/$N"; A="$N"
    else echo "usage: ./build.sh --hot <Name>   modules: $(echo $MODULES)   samples: $(ls samples 2>/dev/null | tr '\n' ' ')"; exit 1; fi
    build "$P"; copy_hot "$P" "$A"; echo "built and copied $A ($V)"
    if running; then curl -s -m 30 -X POST "127.0.0.1:8765/hot/reload?name=$A"; echo; else echo "(game not answering: it loads at the next start)"; fi
    exit 0
fi

build src/LunarLink
# manifest.json beside the core: the version, a fingerprint of the core's source and one of each module's, so the
# in-game updater can tell what changed: the core (a restart) or only some modules (they reload in place). The
# fingerprints are of the source, not the DLLs, which carry their build time. The core's leaves out its [BepInPlugin]
# version line (a release that only bumps it hasn't changed the core).
fingerprint() { (cat Directory.Build.props; find "$1" -type f -not -path '*/bin/*' -not -path '*/obj/*' | LC_ALL=C sort | xargs cat) | grep -v 'BepInPlugin("lunarlink"' | sha256sum | cut -c1-64; }
CORE_VERSION=$(grep -o 'BepInPlugin("lunarlink", "LunarLink", "[^"]*")' src/LunarLink/Server.cs | grep -o '[0-9][0-9.]*')
MODS_JSON=$(for m in $MODULES; do printf '"LunarLink.%s": "%s", ' "$m" "$(fingerprint "src/LunarLink.$m")"; done | sed 's/, $//')
printf '{"version": "%s", "coreHash": "%s", "modules": {%s}}\n' "$CORE_VERSION" "$(fingerprint src/LunarLink)" "$MODS_JSON" > src/LunarLink/bin/Release/net6.0/manifest.json
for m in $MODULES; do build "src/LunarLink.$m"; done
echo "built the core and $(echo $MODULES | wc -w) modules ($V)"
[ "$1" = "--install" ] || exit 0

D="$GAME/BepInEx/plugins/LunarLink"; mkdir -p "$D"
# the game running: staged the way the in-game updater does it - the loaded core is renamed (a loaded DLL can be renamed,
# not overwritten) and the new one put in its place, and the modules wait in plugins/LunarLink/update/ (a new module
# must not run on the old core); the next start loads the new core, which moves them into BepInEx/hot/
if running; then
    cp src/LunarLink/bin/Release/net6.0/LunarLink.dll "$D/LunarLink.dll.new"
    rm -f "$D/LunarLink.dll.old"; mv "$D/LunarLink.dll" "$D/LunarLink.dll.old"; mv "$D/LunarLink.dll.new" "$D/LunarLink.dll"
    echo "$V" > "$D/installed-version"
    cp src/LunarLink/bin/Release/net6.0/manifest.json "$D/manifest.json"
    rm -rf "$D/update"; mkdir -p "$D/update"
    for m in $MODULES; do cp "src/LunarLink.$m/bin/Release/net6.0/LunarLink.$m.dll" "$D/update/"; done
    echo "staged $V: the core and $(echo $MODULES | wc -w) modules; restart the game to finish (the core loads only at start)"
    exit 0
fi
cp src/LunarLink/bin/Release/net6.0/LunarLink.dll "$D/LunarLink.dll.new" && mv -f "$D/LunarLink.dll.new" "$D/LunarLink.dll"
echo "$V" > "$D/installed-version"
cp src/LunarLink/bin/Release/net6.0/manifest.json "$D/manifest.json"
for m in $MODULES; do copy_hot "src/LunarLink.$m" "LunarLink.$m"; done
# a module that no longer exists in src/ is taken out of the game
for f in "$HOT"/LunarLink.*.dll; do
    [ -e "$f" ] || continue
    m=$(basename "$f" .dll); m=${m#LunarLink.}
    echo " $MODULES " | tr '\n' ' ' | grep -q " $m " || { rm -f "$HOT/LunarLink.$m.dll" "$HOT/LunarLink.$m.pdb"; echo "removed the old module $m"; }
done
echo "installed $V: the core and $(echo $MODULES | wc -w) modules (loads on the next game start)"
