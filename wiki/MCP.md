# MCP

LunarLink comes with an [MCP](https://modelcontextprotocol.io) server, so an AI assistant (Claude Desktop, Claude Code,
Cursor, VS Code or any other MCP client) can use the running game through the API. Ask *"is my base working?"*,
*"which catchers are full?"* or *"who's online in my crew game?"* and it calls the endpoints to find out.

It's in the repo's [`mcp/`](https://github.com/swheettaos/lunarlink/tree/main/mcp) folder: a small Python program that
runs **outside the game** and talks to `http://127.0.0.1:8765/`. Nothing goes in the game folder.

## Set it up

You need Python 3.10+ and [uv](https://docs.astral.sh/uv/) (or pipx). In Claude Code:

```bash
claude mcp add --scope user lunarlink -- uvx --from "git+https://github.com/swheettaos/lunarlink#subdirectory=mcp" lunarlink-mcp
```

In Claude Desktop (Settings → Developer → Edit Config), and most other clients:

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

The [mcp/README](https://github.com/swheettaos/lunarlink/tree/main/mcp#readme) has pipx, other clients and every
option.

## What it does

- **Its tools are the game's endpoints**, read at start from `GET /help?details=1`: one per endpoint, named after its
  path (`/base/health` is `base_health`). LunarLink's own come with their description, what they're for and their
  parameters, as in the [[API Reference]]. Plugins' endpoints (Crew Tools, Quantum Conveyors, your hot plugins) come
  too, with their one-line help.
- **Reads only by default.** It offers every `GET` and the `POST` endpoints that only ask (`/structures/check`,
  `/belts/check`, `/update/check`). Add `--allow-writes` after `lunarlink-mcp` for the ones that change the game; those
  still need LunarLink's own `allow-writes` file ([[Settings]]). Tools that remove, kick or ban are marked destructive,
  so the client can ask before running them.
- **Small answers.** Every tool also takes `fields`, `where`, `limit` and `count`, so the assistant gets the part it
  needs: `catchers` with `where: "full=true"` and `count: true` answers `{"storeKg":100,"catchers":48}` instead of
  every catcher. `lunarlink_request` calls any endpoint by method and path, for one that has no tool of its own.
- **Game not running, or no moon loaded?** Every tool says so plainly: LunarLink isn't answering, or `no world loaded
  yet`. Start the game and load a save; `lunarlink_refresh` re-reads the tool list (the server also does that itself
  once the game answers).
- **Another machine:** `--base-url http://<address>:8765/` and the game's **Access key** with `--key` or
  `LUNARLINK_KEY` ([[Settings]]).

## Good questions to ask

- "Run base_health round 0, 1992, -11 with a 150 m radius: what's wrong?"
- "Which machines are stuck, and what does the game say about each?" (`base_health`)
- "How's the power: any grid short?" (`power`)
- "What's eating the frame rate?" (`perf`, `perf_fixes`; see [[Performance Tools]])
- "Is a 1 m foundation buildable at 12.5, 1992.1, -4?" (`structures_check`, which builds nothing)
- "Who's in my crew game, and has anyone been caught cheating?" (`crew_now`, `crew_catches`, with Crew Tools)

[[API Recipes]] has whole jobs worth asking for, step by step.
