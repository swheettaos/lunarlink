# LunarLink MCP server

Lets an AI assistant (Claude Desktop, Claude Code, or any other [MCP](https://modelcontextprotocol.io) client) use a
running Moon Sweepers game through LunarLink's HTTP API. You can ask things like *"is the base round the spawn
working?"*, *"which machines are stuck and why?"* or *"who's in my crew game?"*, and it calls the endpoints to find
out.

- **The tools come from the game.** At start it reads `GET /help?details=1` and makes one tool per endpoint.
  LunarLink's own endpoints come with what they do, what they're for and their parameters. Every plugin's endpoints
  come too (Crew Tools' `/crew/*`, Quantum Conveyors' `/quantum/*`, your own hot plugin's), each with its one-line
  help. A plugin you add later shows up after a refresh.
- **Reads only, unless you say otherwise.** By default it offers only the tools that change nothing: every `GET`, and
  the `POST` ones that only ask (`/structures/check`, `/belts/check`, `/update/check`). Start it with
  `--allow-writes` to add the rest (building, removing, moving the player, kicking...). Even then, LunarLink refuses
  changes until its own `allow-writes` file exists, as it does for any client.
- **It runs outside the game.** It's a small Python program that talks to `http://127.0.0.1:8765/`; nothing is
  installed in the game folder.

## Install

You need Python 3.10 or newer and one of [uv](https://docs.astral.sh/uv/) (recommended) or
[pipx](https://pipx.pypa.io/). Nothing else: the right packages are fetched on first run.

With uv there's nothing to install: the client starts it with `uvx`, which fetches it the first time:

```bash
uvx --from "git+https://github.com/swheettaos/lunarlink#subdirectory=mcp" lunarlink-mcp --list
```

With pipx, install it once; then the command is just `lunarlink-mcp`:

```bash
pipx install "git+https://github.com/swheettaos/lunarlink#subdirectory=mcp"
```

`--list` prints the tools it would offer (with the game running) and exits: a quick check that it can reach the game.

To stay on one release's version instead of `main`, add its tag, for example
`git+https://github.com/swheettaos/lunarlink@v1.0.0#subdirectory=mcp`. The MCP server is in releases from 1.0.0 on.

## Add it to your assistant

### Claude Code

```bash
claude mcp add --scope user lunarlink -- uvx --from "git+https://github.com/swheettaos/lunarlink#subdirectory=mcp" lunarlink-mcp
```

(With pipx: `claude mcp add --scope user lunarlink -- lunarlink-mcp`.) Drop `--scope user` to add it to the current
project only. Check it with `claude mcp list`, or `/mcp` inside Claude Code.

### Claude Desktop

Settings → Developer → Edit Config opens `claude_desktop_config.json`. Add LunarLink under `mcpServers`:

```json
{
  "mcpServers": {
    "lunarlink": {
      "command": "uvx",
      "args": ["--from", "git+https://github.com/swheettaos/lunarlink#subdirectory=mcp", "lunarlink-mcp"]
    }
  }
}
```

With pipx, use `"command": "lunarlink-mcp"` and no `args`. Restart Claude Desktop; the tools are under the tools menu
in a new chat. If it can't find `uvx`, give its full path (`which uvx`, or `where uvx` on Windows).

### Other clients

Any MCP client that starts a local (stdio) server works the same way: the command is `uvx` with the arguments
`--from git+https://github.com/swheettaos/lunarlink#subdirectory=mcp lunarlink-mcp`, or `lunarlink-mcp` after a
pipx install. Most (Cursor, VS Code, Windsurf, Zed...) take the same `mcpServers` block as Claude Desktop above.

## Options

Put options after `lunarlink-mcp`, in the client's command or `args`:

| Option | Default | What it does |
|---|---|---|
| `--allow-writes` | off | Also offer the tools that change the game. Without it, the assistant can only look |
| `--base-url URL` | `http://127.0.0.1:8765/` | Where LunarLink answers. Also `LUNARLINK_URL` |
| `--key KEY` | none | The game's **Access key**, for a game on another machine (sent as `X-LunarLink-Key`). Also `LUNARLINK_KEY` |
| `--timeout SECONDS` | 60 | How long to wait for an answer |
| `--list` | | Print the tools and exit |

For example, writes allowed, in Claude Code:

```bash
claude mcp add --scope user lunarlink -- uvx --from "git+https://github.com/swheettaos/lunarlink#subdirectory=mcp" lunarlink-mcp --allow-writes
```

**A game on another machine:** set LunarLink's `Address` and `Access key` there (see
[Settings](https://github.com/swheettaos/lunarlink/wiki/Settings)), then give this server `--base-url
http://192.168.1.20:8765/` and the key. Prefer `LUNARLINK_KEY` (the `env` block of the client's config) to `--key`, so
the key isn't in the command line.

## What the assistant sees

- **One tool per endpoint**, named after its path: `/base/health` is `base_health`, `/crew/players` is
  `crew_players`. Query parameters are the tool's arguments; an endpoint that takes a JSON body has a `body` argument
  with its fields. A plugin's tool also takes `query`, any other parameters by name, since a plugin's help names them
  only in its text.
- **Marked for what it does:** every reading tool is marked read-only. Tools that change the game are marked as
  such, and the ones that take something away (`…/remove`, kicking or banning, `dev_call`) as destructive, so a client
  can ask you before running them.
- **Answers cut down before they reach the assistant.** Every tool also takes `fields`, `where`, `limit` and `count`,
  which work on the answer's lists (the answer itself, or each list in it). A whole answer can be thousands of
  tokens; the part you want is a few:

  | Arguments | Answer |
  |---|---|
  | `catchers` with `where: "full=true"`, `count: true` | `{"storeKg":100,"catchers":48}` |
  | `catchers` with `where: "kg>140"`, `fields: "id,kg"`, `limit: 3` | three `{id, kg}`, and `(catchers: the first 3 of 18)` |
  | `robots` with `where: "status~cleaning"`, `count: true` | how many robots are cleaning |
  | `crew_players` with `fields: "name,online,switches.build"` | each player's name, whether online, and one switch |

  `where` takes conditions separated by commas, all of which must match: `key=value`, `!=`, `>`, `<`, `>=`, `<=`, and
  `key~text` for "contains" (any case). `a.b` reaches a nested key, in `fields` too. LunarLink still makes the whole
  answer, so for a big base, narrow the call too (a point and radius, a kind) where the endpoint can.
- **`lunarlink_request`** calls any endpoint by method and path, with `query` and `body`, and the same trimming: for a
  plugin's path that has no tool yet, or a parameter a plugin's one-line help doesn't name. In reads-only mode it
  allows `GET` and the `POST`s that only ask, and refuses any other `POST`.
- **`lunarlink_refresh`** re-reads the game's list. The server also reads it again by itself when the list was empty
  (the game wasn't running when it started) or a tool it doesn't know is called.
- **Clear answers when the game isn't there:** if LunarLink doesn't answer, every tool says so (is the game running
  with LunarLink installed?). Before a moon is loaded, most endpoints answer `{"error":"no world loaded yet"}`; the
  tool marks that as an error with what to do. The same goes for `writes disabled` and a wrong access key.
- Very long answers (every belt on a big base) are cut at 200,000 characters, with a note to narrow the call.

## Run it from the repo

For working on it, or without uv or pipx:

```bash
python3 -m venv .venv && .venv/bin/pip install ./mcp
.venv/bin/lunarlink-mcp --list
```

It's one file, [`lunarlink_mcp.py`](lunarlink_mcp.py), on the official [MCP Python SDK](https://github.com/modelcontextprotocol/python-sdk).

## License

GPL-3.0, like the rest of LunarLink.
