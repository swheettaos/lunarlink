#!/bin/bash
# Publish the modules that changed to the module channel: the rolling prerelease "modules", which the in-game updater
# reads (F9's UPDATES tab, /update). Runs in CI after ./build.sh (.github/workflows/build.yml), or by hand. Needs gh,
# signed in with write access.
#
# A module changed when its source fingerprint (manifest.json, written by build.sh) differs from the one published,
# or when the core it's built against changed (modules use the core's internals). Its DLL is uploaded first, then
# modules.json, which lists every module with its fingerprint, SHA-256, size, build time, commit and that commit's
# subject, and the core fingerprint they all need:
#   {"coreHash": "...", "modules": {"LunarLink.Api": {"sourceHash", "sha256", "size", "built", "commit", "note", "url"}}}
# A download caught between the two fails its SHA-256 check and is simply offered again.
set -e
cd "$(dirname "$0")/.."
REPO="${GITHUB_REPOSITORY:-swheettaos/lunarlink}"; TAG=modules
OUT=src/LunarLink/bin/Release/net6.0
[ -f "$OUT/manifest.json" ] || { echo "build first: ./build.sh"; exit 1; }
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT

if ! gh release view "$TAG" -R "$REPO" >/dev/null 2>&1; then
    gh release create "$TAG" -R "$REPO" --prerelease --target main --title "Module builds" --notes "LunarLink's modules as \
built from main: the in-game updater (F9, UPDATES) offers the ones that changed and reloads them in place. Each needs \
the core named in modules.json; a module built for a newer core waits for the next LunarLink release. Not for \
installing by hand: use the latest release."
fi
gh release download "$TAG" -R "$REPO" -p modules.json -D "$WORK" 2>/dev/null || echo '{"coreHash": null, "modules": {}}' > "$WORK/modules.json"

# which modules to upload, and the new modules.json
python3 - "$OUT/manifest.json" "$WORK/modules.json" "$WORK" "$REPO" "$TAG" <<'EOF'
import hashlib, json, os, subprocess, sys, time
manifest, published, work, repo, tag = sys.argv[1:]
m = json.load(open(manifest)); old = json.load(open(published))
core_changed = old.get("coreHash") != m["coreHash"]
feed = {"coreHash": m["coreHash"], "modules": {}}
upload = []
for name, src in m["modules"].items():
    prev = old.get("modules", {}).get(name)
    if prev and prev.get("sourceHash") == src and not core_changed:
        feed["modules"][name] = prev; continue
    dll = f"src/{name}/bin/Release/net6.0/{name}.dll"
    data = open(dll, "rb").read()
    git = lambda *a: subprocess.run(["git", *a], capture_output=True, text=True).stdout.strip()
    commit = git("log", "-1", "--format=%H", "--", f"src/{name}", "Directory.Build.props") or git("rev-parse", "HEAD")
    feed["modules"][name] = {"sourceHash": src, "sha256": hashlib.sha256(data).hexdigest(), "size": len(data),
        "built": time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime()) + " UTC", "commit": commit[:12],
        "note": git("log", "-1", "--format=%s", commit),
        "url": f"https://github.com/{repo}/releases/download/{tag}/{name}.dll"}
    upload.append(dll)
json.dump(feed, open(os.path.join(work, "modules.new.json"), "w"), indent=1)
open(os.path.join(work, "upload.txt"), "w").write("\n".join(upload))
print(("core changed: every module is rebuilt for it\n" if core_changed and old.get("coreHash") else "") +
      (f"publishing {', '.join(os.path.basename(u)[:-4] for u in upload)}" if upload else "no module changed"))
EOF
[ -s "$WORK/upload.txt" ] || exit 0
xargs -a "$WORK/upload.txt" gh release upload "$TAG" -R "$REPO" --clobber
cp "$WORK/modules.new.json" "$WORK/modules.json"
gh release upload "$TAG" -R "$REPO" --clobber "$WORK/modules.json"
echo "published to https://github.com/$REPO/releases/tag/$TAG"
