#!/usr/bin/env python3
"""LunarLink's endpoints: the routes from the source, with the details in docs/endpoints.toml (what each does, its
parameters, its body, an example and its answer). Writes:
  README.md                         the endpoint tables (between the endpoints markers)
  docs/api.html                     the searchable reference page (src/LunarLink.Api/help.html with this list in it)
  src/LunarLink.Api/endpoints.json  the details, built into the Api module for /help?details=1 and /help.html in the game
The wiki's API Reference is made from the same list by tools/wiki.py (markdown() below).
Run after changing a route or its details:  python3 tools/endpoints.py"""
import json, os, re, sys, tomllib
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
read = lambda *p: open(os.path.join(ROOT, *p)).read()
AREAS = ["World", "Base", "Build", "Player", "Game", "Host", "Dev", "Help"]

def routes(text):
    out = []
    for m in re.finditer(r'Api\.(Get|Post|Ask)\("([^"]+)",\s*"((?:[^"\\]|\\.)*)"', text):
        meth = "GET" if m.group(1) == "Get" else "POST"
        out.append({"method": meth, "path": m.group(2), "help": m.group(3).replace('\\"', '"') + (" (changes nothing)" if m.group(1) == "Ask" else ""),
                    "write": m.group(1) == "Post"})
    return out

def blurb(text):
    b = re.search(r'    // (.*?)\n    (?:static|public) class', text, re.S).group(1)
    return " ".join(l.strip().lstrip("/").strip() for l in b.split("\n"))

def details():
    return tomllib.loads(read("docs", "endpoints.toml"))

def groups():
    """Every group of LunarLink's own endpoints, in order, each endpoint with its details ("d") when it has some."""
    gs = [{"group": "LunarLink", "from": "core", "about": "The core: status and hot plugins.",
           "endpoints": [e for f in ("Pump.cs", "Hot.cs") for e in routes(read("src", "LunarLink", f))]}]
    for area in AREAS:
        t = read("src", "LunarLink.Api", area + "Area.cs")
        gs.append({"group": area, "from": "LunarLink.Api", "about": blurb(t), "endpoints": routes(t)})
    t = read("src", "LunarLink.Window", "BepPlugins.cs")
    gs.append({"group": "BepPlugins", "from": "LunarLink.Window", "about": blurb(t), "endpoints": routes(t)})
    t = read("src", "LunarLink.Perf", "PerfModule.cs")
    gs.append({"group": "Perf", "from": "LunarLink.Perf", "about": blurb(t), "endpoints": routes(t)})
    t = read("src", "LunarLink.Update", "Updater.cs")
    gs.append({"group": "Update", "from": "LunarLink.Update", "about": blurb(t), "endpoints": routes(t)})
    d = details()
    for g in gs:
        for e in g["endpoints"]:
            if e["path"] in d: e["d"] = d[e["path"]]
    return gs

def check(gs):
    """Endpoints with no details, and details for endpoints that are gone."""
    paths = {e["path"] for g in gs for e in g["endpoints"]}
    d = details()
    return sorted(paths - set(d)), sorted(set(d) - paths)

# ---- Markdown, for the wiki's API Reference
def anchor(text):
    return re.sub(r"[^\w\- ]", "", text.lower()).replace(" ", "-")

def cell(s):
    return str(s).replace("|", "\\|").replace("\n", " ")

def md_endpoint(e):
    d = e.get("d", {})
    out = [f"### `{e['method']} {e['path']}`", "", d.get("about", e["help"])]
    if d.get("use"): out += ["", f"**What it's for:** {d['use']}"]
    if e["write"]: out += ["", "Changes the game: needs `allow-writes`."]
    for key, name in (("params", "Parameters"), ("body", "Body (JSON)")):
        if d.get(key):
            out += ["", f"**{name}**", "", "| Name | Type | Default | Meaning |", "|---|---|---|---|"]
            out += [f"| `{cell(n)}` | {cell(t)} | {cell(df) or '-'} | {cell(m)} |" for n, t, df, m in d[key]]
    if d.get("example"):
        out += ["", "**Example**", "", "```http", d["example"].rstrip("\n"), "```"]
    if d.get("answer"):
        a = d["answer"].rstrip("\n"); lang = "json" if a[:1] in "{[" else "text"
        out += ["", "**Answer**", "", f"```{lang}", a, "```"]
    for key, name in (("errors", "Errors"), ("notes", "Notes")):
        if d.get(key): out += ["", f"**{name}**", ""] + [f"- {x}" for x in d[key]]
    return "\n".join(out)

def markdown():
    gs = groups()
    rows = []
    for g in gs:
        rows.append(f"| **{g['group']}** ({g['from']}) | |")
        rows += [f"| [`{e['method']} {e['path']}`](#{anchor(e['method'] + ' ' + e['path'])}) | {cell(e['help'])} |" for e in g["endpoints"]]
    index = "| Endpoint | What it does |\n|---|---|\n" + "\n".join(rows)
    body = "\n\n".join(f"## {g['group']} ({g['from']})\n\n{g['about']}\n\n" + "\n\n".join(md_endpoint(e) for e in g["endpoints"]) for g in gs)
    return index + "\n\n" + body

# ---- the README tables
def table(g):
    rows = "\n".join(f"| `{e['method']} {e['path']}` | {cell(e['help'])} |" for e in g["endpoints"])
    return f"### {g['group']} ({g['from']})\n\n{g['about']}\n\n| Endpoint | What it does |\n|---|---|\n{rows}\n"

def main():
    gs = groups()
    missing, gone = check(gs)
    for p in missing: print(f"warning: {p} has no details in docs/endpoints.toml", file=sys.stderr)
    for p in gone: print(f"warning: docs/endpoints.toml has {p}, which no route serves", file=sys.stderr)

    # the details built into the Api module, by path
    with open(os.path.join(ROOT, "src", "LunarLink.Api", "endpoints.json"), "w") as f:
        json.dump({e["path"]: e["d"] for g in gs for e in g["endpoints"] if "d" in e}, f, ensure_ascii=False, separators=(",", ":"))

    # docs/api.html
    page = read("src", "LunarLink.Api", "help.html")
    data = dict(groups=[{"group": g["group"], "from": g["from"],
                         "endpoints": [{k: e[k] for k in ("method", "path", "help", "d") if k in e} for e in g["endpoints"]]} for g in gs],
                note="LunarLink's own endpoints; the running game's /help.html adds the plugins'")
    with open(os.path.join(ROOT, "docs", "api.html"), "w") as f:
        page = page.replace('<a class="home" href="https://swheettaos.github.io/lunarlink/">', '<a class="home" href="./">')   # on the site: its home page
        f.write(page.replace("/*DATA*/null", json.dumps(data, ensure_ascii=False).replace("</", "<\\/")))

    # README tables
    readme = read("README.md")
    start, end = "<!-- endpoints:start (tools/endpoints.py) -->", "<!-- endpoints:end -->"
    i, j = readme.index(start) + len(start), readme.index(end)
    with open(os.path.join(ROOT, "README.md"), "w") as f:
        f.write(readme[:i] + "\n" + "\n".join(table(g) for g in gs) + readme[j:])
    n = sum(len(g["endpoints"]) for g in gs)
    print(f"{n} endpoints in {len(gs)} groups, {n - len(missing)} with details -> README.md, docs/api.html, src/LunarLink.Api/endpoints.json")

if __name__ == "__main__":
    main()
