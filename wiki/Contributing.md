# Contributing

## Reporting a problem

Open an [issue](https://github.com/swheettaos/lunarlink/issues) with:

- what you did and what happened (the request, if it was an API call, and its answer);
- the relevant lines of `BepInEx/LogOutput.log`, and of `BepInEx/plugins/LunarLink/requests.log` after a freeze;
- your game and BepInEx versions (and MoonFramework's, if you have it), and the other mods you run.

## Sending a change

Pull requests are welcome. Fork the repo, make the change on a branch, and open a pull request against `main`; the
owner reviews and merges every one. Checks on pull requests from outside contributors wait for the owner's approval
before they run.

Before you send it:

- `./build.sh` builds the core and every module without warnings.
- If you added or changed an endpoint, describe it in `docs/endpoints.toml` (what it does, its parameters, an example
  and a real answer), then run `python3 tools/endpoints.py`: it regenerates the README's endpoint tables, `docs/api.html`
  and the details built into the game's `/help`, and warns about any endpoint left undescribed.
- A change to a module can be tried with the game running: `./build.sh --hot Api` (or `Perf`, `Window`).
- New endpoints follow the pattern (`GET` to read, `POST .../check|create|modify|remove` to change) and get a one-line
  description in their registration, which is what `/help` shows.

## Building on GitHub

The `build` workflow builds every push to `main` on GitHub's runners. LunarLink compiles against the game's own DLLs,
which can't be public, so the workflow reads them from a private repo with a key only this repository has. On a fork,
and on a pull request from one, that build is skipped (with a note saying so): build and test locally with
`./build.sh` against your own game, which is what testing a change needs anyway.

A fork that wants its own CI can make the same: a private repo filled by `tools/refs.sh` from your game, a read-only
deploy key on it, the key's private half as the secret `REFS_DEPLOY_KEY`, and the variable `REFS_REPO` set to
`you/your-refs-repo`.

## The code

| Folder | What |
|---|---|
| `src/LunarLink` | The core: `Server.cs`, `Pump.cs`, `Api.cs` (route table), `Hot.cs` (loader), `Bridges.cs`, `BepPlugins.cs`, `PluginHttp.cs` |
| `src/LunarLink.Api` | The endpoints, one file per area (`WorldArea.cs`, `BuildArea.cs`, ...) and their helpers |
| `src/LunarLink.Perf` | Measuring and the fps fixes |
| `src/LunarLink.Window` | The F9 window |
| `samples/HelloHot` | The example hot plugin |
| `docs/` | The GitHub Pages site: the API reference and the plugin guide |
| `docs/endpoints.toml` | Every endpoint's details: parameters, examples, answers |
| `tools/publish-modules.sh` | Publishes the modules that changed to the module channel (CI runs it after every build on `main`) |
| `tools/refs.sh` | Refreshes the private build references from the game, after a game or BepInEx update |
| `tools/endpoints.py` | Writes the endpoint tables, `docs/api.html` and the in-game details from the code and `docs/endpoints.toml` |

## License

LunarLink is licensed under the [GPL-3.0](https://github.com/swheettaos/lunarlink/blob/main/LICENSE). Contributions are
accepted under the same license.
