#!/usr/bin/env python3
"""Builds the wiki from wiki/ into an output folder (default _wiki/): every page as it is, with each
<!-- include: ... --> marker filled in, so the wiki never holds a stale copy of something kept elsewhere:
  <!-- include: endpoints -->          every LunarLink endpoint with its details (tools/endpoints.py, docs/endpoints.toml)
  <!-- include: docs/HOT_PLUGINS.md --> that document, with its links made wiki links
  <!-- include: samples/... .cs -->     that file's source
.github/workflows/wiki.yml runs this on every push to main and publishes the result to the GitHub wiki.
Run it yourself to look:  python3 tools/wiki.py [out]"""
import os, re, shutil, sys
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "_wiki"))
read = lambda p: open(os.path.join(ROOT, p)).read()

def endpoints():
    sys.path.insert(0, os.path.join(ROOT, "tools"))
    import endpoints as ep
    return ep.markdown()

def guide():
    s = read("docs/HOT_PLUGINS.md").replace("# Writing a hot plugin", "# Writing a Hot Plugin", 1)
    a = "You can write a new one from `samples/HelloHot`,"
    if a not in s: raise SystemExit("docs/HOT_PLUGINS.md changed: update guide() in tools/wiki.py")
    s = s.replace(a, "You can write a new one from [`samples/HelloHot`](https://github.com/swheettaos/lunarlink/tree/main/samples/HelloHot) "
                     "([[Plugin Examples]] has its code and more),")
    s = s.replace("## Start from HelloHot", "See [[Hot Plugins]] for how loading and reloading work, and [[Plugins with Buildings]] for "
                  "plugins that add buildings.\n\n## Start from HelloHot", 1)
    return s.replace("copy `Directory.Build.props` from here",
                     "copy [`Directory.Build.props`](https://github.com/swheettaos/lunarlink/blob/main/Directory.Build.props) from the repo")

def include(m):
    what = m.group(1).strip()
    if what == "endpoints": return endpoints()
    if what == "docs/HOT_PLUGINS.md": return guide()
    if what.endswith(".cs"): return read(what).rstrip()
    raise SystemExit(f"unknown include: {what}")

if os.path.isdir(OUT):
    for f in os.listdir(OUT):
        if f != ".git": (shutil.rmtree if os.path.isdir(os.path.join(OUT, f)) else os.remove)(os.path.join(OUT, f))
os.makedirs(OUT, exist_ok=True)
pages = 0
for f in sorted(os.listdir(os.path.join(ROOT, "wiki"))):
    if not f.endswith(".md"): continue
    text = re.sub(r"<!-- include: (.+?) -->", include, read(os.path.join("wiki", f)))
    open(os.path.join(OUT, f), "w").write(text); pages += 1
print(f"{pages} pages -> {OUT}")
