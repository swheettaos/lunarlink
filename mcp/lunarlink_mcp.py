"""LunarLink MCP server: Moon Sweepers' LunarLink API as tools for an AI assistant.

The tools are made at start from the running game's own list, GET /help?details=1: one tool per endpoint, LunarLink's
own with their parameters and body described, and every plugin's endpoints (Crew Tools' /crew/* and the like) too.
Reads only, unless started with --allow-writes; LunarLink itself still needs its allow-writes file for any change.

    lunarlink-mcp [--allow-writes] [--base-url http://127.0.0.1:8765/] [--key KEY] [--timeout 60]
"""
from __future__ import annotations

import argparse
import json
import operator
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

import anyio
import mcp_types as types
from mcp.server.lowlevel.server import NotificationOptions, Server
from mcp.server.stdio import stdio_server
from mcp.server.subscriptions import InMemorySubscriptionBus, ListenHandler
from mcp.shared.subscriptions import ToolsListChanged
from mcp_types.version import MODERN_PROTOCOL_VERSIONS

__version__ = "0.1.0"

REFRESH = "lunarlink_refresh"
REQUEST = "lunarlink_request"
SKIP = {"/help.html"}  # a page for a browser, not data
DESTRUCTIVE = {"remove", "delete", "kick", "ban"}  # a path part or flag that takes something away
MAX_CHARS = 200_000  # a longer answer is cut, with a note on how to narrow it

INSTRUCTIONS = """Tools for Moon Sweepers through the LunarLink plugin's HTTP API, made from the running game's own \
endpoint list. Start with `status`: `world: true` means a moon is loaded; most tools answer \
{"error":"no world loaded yet"} until then. World points are metres from the moon's centre (its surface is about \
2000 m out). If the game was started or a plugin was loaded after this server, call `lunarlink_refresh` to re-read \
the tool list. Every tool takes `fields`, `where`, `limit` and `count` to cut its answer down before it reaches you; \
use them on big answers. They work on the answer's lists (the answer itself, or each list in it): `where` keeps the \
items that match every condition (key=value, != > < >= <=, key~text for contains; a.b for nested keys), `count` \
answers with how many there are instead, `limit` keeps the first N, `fields` keeps only those keys of each item. \
E.g. `catchers` with where="full=true", count=true answers {"storeKg":100,"catchers":48}. `lunarlink_request` calls any \
endpoint by method and path, for one with no tool of its own."""

# The answer, cut down here before it reaches the assistant (LunarLink still makes the whole answer).
# Short, as every tool carries them; the server's instructions say more.
TRIM = {
    "fields": {"type": "string", "description": "Keys to keep per item: id,pos,a.b"},
    "where": {"type": "string", "description": "Items to keep: full=true,kg>100,name~text"},
    "limit": {"type": "integer", "minimum": 0, "description": "Items per list"},
    "count": {"type": "boolean", "description": "Only how many items"},
}


class Link:
    """The game's HTTP API: where it is, the key for another machine, and the tools made from its /help."""

    def __init__(self, base_url: str, key: str | None, timeout: float, allow_writes: bool):
        self.base = base_url.rstrip("/") + "/"
        self.key, self.timeout, self.allow_writes = key, timeout, allow_writes
        self.tools: dict[str, dict] = {}  # name -> {tool, method, path, ...}
        self.hidden: set[str] = set()  # names of the writes left out in reads-only mode
        self.problem: str | None = "not read yet"  # why the list is empty, when it is

    # --- HTTP -------------------------------------------------------------------------------------------------------
    def request(self, method: str, path: str, query: dict[str, str] | None = None, body=None, timeout=None) -> str:
        url = self.base + path.lstrip("/")
        if query:
            url += "?" + urllib.parse.urlencode(query)
        data = json.dumps(body).encode() if body is not None else (b"" if method == "POST" else None)
        req = urllib.request.Request(url, data=data, method=method)
        if body is not None:
            req.add_header("Content-Type", "application/json")
        if self.key:
            req.add_header("X-LunarLink-Key", self.key)
        try:
            with urllib.request.urlopen(req, timeout=timeout or self.timeout) as r:
                return r.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:  # LunarLink's own errors come as JSON bodies
            text = e.read().decode("utf-8", "replace")
            return text or json.dumps({"error": f"HTTP {e.code} {e.reason}"})

    def down(self, e: Exception) -> str:
        return (f"LunarLink isn't answering at {self.base} ({getattr(e, 'reason', e)}). Is Moon Sweepers running with "
                f"LunarLink installed? Start the game, then call {REFRESH}.")

    # --- the tool list ----------------------------------------------------------------------------------------------
    def load(self, timeout: float = 5) -> bool:
        """Read /help?details=1 and make the tools. Returns whether the list changed."""
        before = sorted(self.tools)
        try:
            text = self.request("GET", "/help", {"details": "1"}, timeout=timeout)
            groups = json.loads(text)["groups"]
        except (urllib.error.URLError, OSError) as e:
            self.tools, self.hidden, self.problem = {}, set(), self.down(e)
            return before != []
        except (ValueError, KeyError, TypeError):
            self.tools, self.hidden = {}, set()
            self.problem = "LunarLink's /help didn't answer with its endpoint list: " + text[:300]
            return before != []
        tools, hidden = {}, set()
        for g in groups:
            for e in g.get("endpoints", []):
                t = make_tool(e, g.get("group", ""), g.get("from", ""))
                if t is None:
                    continue
                if t["write"] and not self.allow_writes:
                    hidden.add(t["tool"].name)
                    continue
                name, n = t["tool"].name, 2
                while name in tools:
                    name, n = f"{t['tool'].name}_{n}", n + 1
                t["tool"].name = name
                tools[name] = t
        self.tools, self.hidden, self.problem = tools, hidden, None
        return before != sorted(tools)

    def listing(self) -> list[types.Tool]:
        if self.problem:
            note = "Nothing else is listed: " + self.problem
        else:
            note = f"{len(self.tools)} tools from the game."
            if self.hidden:
                note += (f" {len(self.hidden)} that change the game are left out: this server runs reads-only (start it "
                         f"with --allow-writes for them).")
        refresh = types.Tool(
            name=REFRESH,
            description="Re-read the game's endpoint list and remake the tools: after starting the game or loading "
                        "a moon, or after a plugin was added, reloaded or turned off. " + note,
            input_schema={"type": "object", "properties": {}},
            annotations=types.ToolAnnotations(title="Refresh LunarLink's tools", read_only_hint=True,
                                              idempotent_hint=True, open_world_hint=False))
        reads = "GET, or a POST that only asks (a /check)" if not self.allow_writes else "GET or POST"
        request = types.Tool(
            name=REQUEST,
            description=f"Call any LunarLink endpoint by method and path: one with no tool of its own (a plugin's new "
                        f"path), or with parameters its tool doesn't list. {reads}." +
                        ("" if self.allow_writes else " This server runs reads-only, so changes are refused."),
            input_schema={"type": "object", "required": ["path"], "properties": {
                "method": {"type": "string", "enum": ["GET", "POST"], "description": "GET (default) or POST"},
                "path": {"type": "string", "description": "The path, e.g. /structures; a ?query on it is read too"},
                "query": {"type": "object", "description": "Query parameters by name"},
                "body": {"description": "A JSON body (POST)"},
                **TRIM}},
            annotations=types.ToolAnnotations(title="Any LunarLink endpoint", read_only_hint=not self.allow_writes,
                                              destructive_hint=True if self.allow_writes else None,
                                              open_world_hint=False))
        return [refresh, request] + [t["tool"] for t in self.tools.values()]

    # --- a call -----------------------------------------------------------------------------------------------------
    def call(self, t: dict, args: dict) -> tuple[str, bool]:
        args = dict(args or {})
        trim = {k: args.pop(k) for k in t["trim"] if k in args}
        query, body = {}, None
        for k, v in args.items():
            if k == "body":
                body = v
            elif k == "query" and isinstance(v, dict):
                query.update({str(a): flat(b) for a, b in v.items()})
            elif v is not None:
                query[k] = flat(v)
        try:
            text = self.request(t["method"], t["path"], query, body)
        except (urllib.error.URLError, OSError) as e:
            return self.down(e), True
        return answer(text, trim)

    def raw(self, args: dict) -> tuple[str, bool]:
        """lunarlink_request: any endpoint by method and path."""
        method = str(args.get("method") or "GET").upper()
        path, _, qs = str(args.get("path") or "").partition("?")
        if method not in ("GET", "POST") or not path.startswith("/"):
            return "Give a method (GET or POST) and a path starting with /, e.g. /structures.", True
        asks = path.endswith("/check") or any(t["path"] == path and t["method"] == "POST" and not t["write"]
                                              for t in self.tools.values())
        if method == "POST" and not asks and not self.allow_writes:
            return (f"POST {path} may change the game, and this server runs reads-only: the player can restart it "
                    f"with --allow-writes.", True)
        query = {k: v[-1] for k, v in urllib.parse.parse_qs(qs, keep_blank_values=True).items()}
        query.update({str(k): flat(v) for k, v in (args.get("query") or {}).items() if v is not None})
        trim = {k: args[k] for k in TRIM if k in args}
        try:
            text = self.request(method, path, query, args.get("body"))
        except (urllib.error.URLError, OSError) as e:
            return self.down(e), True
        return answer(text, trim)


def flat(v) -> str:
    if isinstance(v, bool):
        return "1" if v else "0"
    if isinstance(v, (list, tuple)):
        return ",".join(flat(x) for x in v)
    if isinstance(v, float) and v.is_integer():
        return str(int(v))
    return str(v)


def answer(text: str, trim: dict | None = None) -> tuple[str, bool]:
    """The answer as the tool's text (cut down by trim), and whether it's an error."""
    error = False
    try:
        j = json.loads(text)
        if isinstance(j, dict) and j.get("error"):
            error = True
            msg = str(j["error"])
            if "no world loaded" in msg:
                text += "\nNo moon is loaded: load a save or join a game, then try again (`status` shows `world`)."
            elif "writes disabled" in msg:
                text += ("\nLunarLink refuses changes until the file BepInEx/plugins/LunarLink/allow-writes exists "
                         "(an empty file) in the game folder. Creating it is up to the player.")
            elif "access key" in msg:
                text += "\nGive this server the key with --key (or LUNARLINK_KEY): the game's API key setting."
        elif trim and any(v not in (None, "", False) for v in trim.values()):
            try:
                j, notes = trimmed(j, trim)
            except ValueError as e:
                return str(e), True
            text = json.dumps(j, separators=(",", ":")) + "".join(f"\n({n})" for n in notes)
    except ValueError:
        pass
    if len(text) > MAX_CHARS:
        text = text[:MAX_CHARS] + (f"\n... cut at {MAX_CHARS} of {len(text)} characters: narrow the call (a point and "
                                   f"radius, a kind, ids) for the rest.")
    return text, error


# --- cutting an answer down --------------------------------------------------------------------------------------
MISSING = object()
OPS = {">=": operator.ge, "<=": operator.le, "!=": None, "=": None, ">": operator.gt, "<": operator.lt, "~": None}


def get(x, key: str):
    """x's value at a.b (a list's item by its index), or MISSING."""
    for part in key.split("."):
        if isinstance(x, dict) and part in x:
            x = x[part]
        elif isinstance(x, list) and part.lstrip("-").isdigit() and -len(x) <= int(part) < len(x):
            x = x[int(part)]
        else:
            return MISSING
    return x


def same(a, b) -> bool:
    return a == b or str(a).lower() == str(b).lower()


def condition(text: str):
    m = re.fullmatch(r"\s*([\w.\-]+)\s*(>=|<=|!=|=|>|<|~)\s*(.*?)\s*", text)
    if not m:
        raise ValueError(f"Can't read the where condition {text!r}: use key=value, key>number, key~text "
                         f"(= != > < >= <= ~), several with commas.")
    key, op, raw = m.groups()
    if raw[:1] in "<>=!~" and raw:
        raise ValueError(f"Can't read the where condition {text!r}: one operator (= != > < >= <= ~) between key and value.")
    try:
        want = json.loads(raw)
    except ValueError:
        want = raw

    def test(item) -> bool:
        v = get(item, key)
        if v is MISSING:
            return op == "!="
        if op == "=":
            return same(v, want)
        if op == "!=":
            return not same(v, want)
        if op == "~":
            return str(raw).lower() in (v if isinstance(v, str) else json.dumps(v)).lower()
        try:
            return bool(OPS[op](v, want))
        except TypeError:
            return False
    return test


def trimmed(j, trim: dict):
    """The answer with its lists filtered (where), cut (limit), counted (count) and narrowed (fields); and notes."""
    fields = [f.strip() for f in str(trim.get("fields") or "").split(",") if f.strip()]
    tests = [condition(c) for c in str(trim.get("where") or "").split(",") if c.strip()]
    limit, count, notes = trim.get("limit"), bool(trim.get("count")), []

    def pick(x):
        if not fields or not isinstance(x, dict):
            return x
        return {f: v for f in fields if (v := get(x, f)) is not MISSING}

    def one(name: str, items: list):
        if tests:
            items = [x for x in items if all(t(x) for t in tests)]
        if count:
            return len(items)
        if limit is not None and len(items) > int(limit):
            notes.append(f"{name}: the first {int(limit)} of {len(items)}")
            items = items[:int(limit)]
        return [pick(x) for x in items]

    if isinstance(j, list):
        return one("the list", j), notes
    if isinstance(j, dict):
        if not any(isinstance(v, list) for v in j.values()):
            return pick(j), notes
        return {k: one(k, v) if isinstance(v, list) else v for k, v in j.items()
                if isinstance(v, list) or not (count and isinstance(v, dict))}, notes
    return j, notes


# --- an endpoint as a tool ------------------------------------------------------------------------------------------
TYPES = {"number": {"type": "number"}, "int": {"type": "integer"}, "true/false": {"type": "boolean"},
         "1": {"type": "boolean"}, "0": {"type": "boolean"}, "1|0": {"type": "boolean"},
         "[x,y,z]": {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3},
         "[x,y,z,w]": {"type": "array", "items": {"type": "number"}, "minItems": 4, "maxItems": 4}}


def schema_for(kind: str, meaning: str, default: str) -> dict:
    s = dict(TYPES.get(kind) or ({"type": "string", "enum": kind.split("|")} if "|" in kind else {"type": "string"}))
    if kind == "ids":
        s["description"] = "Comma-separated ids. "
    d = (s.get("description", "") + meaning).strip()
    if default:
        d += f" (default: {default})"
    s["description"] = d
    return s


def fields(rows) -> list[tuple[str, dict, str]]:
    """[name, type, default, meaning] rows, "x, y, z" split into one field each."""
    out = []
    for row in rows or []:
        names, kind, default, meaning = (list(row) + ["", "", "", ""])[:4]
        for n in str(names).split(","):
            n = n.strip()
            if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", n):
                out.append((n, schema_for(str(kind), str(meaning), str(default)), str(meaning)))
    return out


def row_text(row) -> str:
    names, kind, default, meaning = (list(row) + ["", "", "", ""])[:4]
    return f"- {names} ({kind}): {meaning}" + (f" (default: {default})" if default else "")


def tool_name(path: str) -> str:
    name = re.sub(r"[^A-Za-z0-9]+", "_", path).strip("_") or "root"
    return name[:60]


def make_tool(e: dict, group: str, source: str) -> dict | None:
    method, raw, help_ = e.get("method", "GET"), e.get("path", ""), e.get("help", "")
    path, _, declared = raw.partition("?")  # a plugin's "POST /crew/players/remove?id=&ban=1"
    if not path.startswith("/") or raw in SKIP or method not in ("GET", "POST"):
        return None
    d = e.get("d") or {}
    reads = method == "GET" or help_.endswith("(changes nothing)") or path.endswith("/check")
    parts = set(path.strip("/").split("/")) | set(urllib.parse.parse_qs(declared, keep_blank_values=True))
    destructive = not reads and (bool(parts & DESTRUCTIVE) or path == "/dev/call")

    props = {}
    if d:
        for n, s, _ in fields(d.get("params")):
            props[n] = s
        if d.get("body"):
            body = {n: s for n, s, _ in fields(d["body"])}
            props["body"] = {"type": "object", "properties": body, "description": "The JSON body"}
        about, use = d.get("about") or help_, d.get("use", "")
    else:
        for n in urllib.parse.parse_qs(declared, keep_blank_values=True):
            if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", n):
                props[n] = {"type": "string"}
        props["query"] = {"type": "object", "additionalProperties": {"type": "string"},
                          "description": "Other query parameters, by name (see the description)"}
        if method == "POST":
            props["body"] = {"type": "object", "description": "A JSON body, if the endpoint takes one"}
        about, use = help_, ""

    trim = [k for k in TRIM if k not in props]  # an endpoint's own parameter of the same name wins
    props.update({k: TRIM[k] for k in trim})

    text = [about.strip()]
    if use:
        text.append("What it's for: " + use.strip())
    if d.get("params"):
        text.append("Parameters:\n" + "\n".join(row_text(r) for r in d["params"]))
    if d.get("body"):
        text.append("Body (the `body` argument, a JSON object):\n" + "\n".join(row_text(r) for r in d["body"]))
    if not d:
        text.append(f"From {source or 'a plugin'} ({group}): {method} {raw}")
    else:
        text.append(f"{method} {path}")
    if not reads:
        text.append("Changes the game (LunarLink needs its allow-writes file).")

    tool = types.Tool(
        name=tool_name(path),
        description="\n\n".join(text),
        input_schema={"type": "object", "properties": props},
        annotations=types.ToolAnnotations(title=f"{method} {path}", read_only_hint=reads,
                                          destructive_hint=None if reads else destructive,
                                          idempotent_hint=True if reads else None, open_world_hint=False))
    return {"tool": tool, "method": method, "path": path, "write": not reads, "trim": trim}


# --- the server -----------------------------------------------------------------------------------------------------
def build(link: Link) -> tuple[Server, InMemorySubscriptionBus]:
    bus = InMemorySubscriptionBus()

    async def changed(ctx) -> None:
        await bus.publish(ToolsListChanged())
        if ctx.protocol_version not in MODERN_PROTOCOL_VERSIONS:
            await ctx.session.send_tool_list_changed()

    async def list_tools(ctx, params) -> types.ListToolsResult:
        if link.problem:  # the game may have started since: try again, briefly
            await anyio.to_thread.run_sync(lambda: link.load(timeout=2))
        return types.ListToolsResult(tools=link.listing())

    async def call_tool(ctx, params: types.CallToolRequestParams) -> types.CallToolResult:
        name, args = params.name, params.arguments or {}
        if name == REFRESH:
            was = link.problem
            if await anyio.to_thread.run_sync(link.load) or (was and not link.problem):
                await changed(ctx)
            text = link.problem or f"{len(link.tools)} tools from the game" + (
                f"; {len(link.hidden)} that change the game left out (reads-only)" if link.hidden else "") + "."
            return result(text, bool(link.problem))
        if name == REQUEST:
            text, error = await anyio.to_thread.run_sync(lambda: link.raw(args))
            return result(text, error)
        t = link.tools.get(name)
        if t is None:  # made before a refresh, or the game wasn't up: look again once
            if await anyio.to_thread.run_sync(link.load):
                await changed(ctx)
            t = link.tools.get(name)
        if t is None and name in link.hidden:
            return result(f"{name} changes the game, and this server runs reads-only: the player can restart it with "
                          f"--allow-writes.", True)
        if t is None:
            return result(link.problem or f"No tool {name}: LunarLink has no such endpoint now. Call {REFRESH}.", True)
        text, error = await anyio.to_thread.run_sync(lambda: link.call(t, args))
        return result(text, error)

    server = Server("lunarlink", version=__version__, title="LunarLink (Moon Sweepers)", instructions=INSTRUCTIONS,
                    website_url="https://github.com/swheettaos/lunarlink",
                    on_list_tools=list_tools, on_call_tool=call_tool, on_subscriptions_listen=ListenHandler(bus))
    return server, bus


def result(text: str, error: bool) -> types.CallToolResult:
    return types.CallToolResult(content=[types.TextContent(text=text)], is_error=error)


def main() -> None:
    p = argparse.ArgumentParser(prog="lunarlink-mcp", description="Moon Sweepers' LunarLink API as MCP tools (stdio).")
    p.add_argument("--allow-writes", action="store_true",
                   help="also offer the tools that change the game (building, removing, moving, kicking...)")
    p.add_argument("--base-url", default=os.environ.get("LUNARLINK_URL", "http://127.0.0.1:8765/"),
                   help="where LunarLink answers (default: %(default)s; or LUNARLINK_URL)")
    p.add_argument("--key", default=os.environ.get("LUNARLINK_KEY"),
                   help="the game's API key, for a game on another machine (or LUNARLINK_KEY)")
    p.add_argument("--timeout", type=float, default=60, help="seconds to wait for an answer (default: %(default)s)")
    p.add_argument("--list", action="store_true", help="print the tools it would offer, and exit")
    p.add_argument("--version", action="version", version=__version__)
    a = p.parse_args()

    link = Link(a.base_url, a.key, a.timeout, a.allow_writes)
    link.load()
    if a.list:
        for t in link.listing():
            ann = t.annotations
            mark = "read " if ann.read_only_hint else "WRITE" + ("!" if ann.destructive_hint else " ")
            print(f"{mark}  {t.name:28} {ann.title}")
        if link.problem:
            print(link.problem, file=sys.stderr)
        return
    print(f"lunarlink-mcp {__version__}: {link.problem or f'{len(link.tools)} tools'} "
          f"({'writes allowed' if a.allow_writes else 'reads only'})", file=sys.stderr)
    server, _ = build(link)

    async def run() -> None:
        async with stdio_server() as (r, w):
            await server.run(r, w, server.create_initialization_options(NotificationOptions(tools_changed=True)))

    anyio.run(run)


if __name__ == "__main__":
    main()
