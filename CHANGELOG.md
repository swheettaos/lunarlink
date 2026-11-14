# Changelog

Each release's section is its notes on the GitHub releases page (`tools/release.sh` copies it there). Only a release that
changes the core needs a game restart; modules and hot plugins always update in place.

## 1.2.0

- **Normal BepInEx plugins can go in `BepInEx/hot/` (experimental):** a plugin not written for LunarLink runs *wrapped* by the new
  **LunarLink.Wrap** module. It loads where BepInEx would load it, and F9's Reload and Disable work on it: its own
  `Unload()` if it has one, then everything it started is undone (threads stopped, timers and native hooks undone,
  event handlers it added elsewhere removed, its Unity objects destroyed, its Harmony patches taken out). Its Unity
  types get a new name in each later copy, since Il2Cpp keeps a name for good. A plugin that asks for its own location
  is told its DLL's path. F9 marks it **wrapped**, and its **?** lists what the wrapper found; a plugin that hooks the
  game in a way the wrapper can't undo turns off at the next start instead (**off at restart**). Needs a restart once,
  for the core.
- **MoonFramework must stay in `BepInEx/plugins/`; it can't be hot-swapped.** The wrapper refuses it, and any library
  another installed plugin is built on, with a message in F9.
- **A hot plugin can have its own folder:** `BepInEx/hot/<Name>/<Name>.dll`, with the files it reads and the DLLs it
  needs beside it (those load with it).
- **LunarLink's own modules load first** at game start.
- **Wrapped plugins with their own content reload safely:** their asset bundles are unloaded when they stop, so the
  new copy can load them again; while a world is loaded their Reload waits for the main menu (F9: **Save & main menu**
  saves the game's own way and goes there), since a copy swapped in mid-world can't save the world it didn't set up.
- **A reload that keeps failing is stopped:** errors from a plugin's code in the 10 s after its reload are counted, and
  at 20 it's stopped with "restart the game" on its row, instead of throwing every frame.
- **Modules have version numbers:** a release's modules carry its number (1.1.0), and each later build on the module
  channel counts up from it (1.1.0.1, 1.1.0.2...). F9 shows them on HOT PLUGINS, and UPDATES says which is available
  and which you have; `GET /update` lists both (`version`, `installed`), and so does the channel's `modules.json`.
  A module built before versions is offered once more so it gets its number.
- **`POST /game/money/modify?add=` or `?set=`:** change the team's money (the host only, in a crew game; logged). It
  goes through the game's own wallet, so Crew Tools logs it as the host's own change, not as cheating.
- **F9 shows every hot plugin's version**, not only LunarLink's modules: the one in its `[BepInPlugin]` (Crew Tools
  v1.2.4, Who Left The Light On? v0.1.1).
- **F9 → UPDATES says when a restart is still needed:** after an update that brought a new core, checking again said
  "up to date" although the game still ran the old core. It now says "restart the game to finish" until it's done
  (`GET /update`: `loaded`, `restartPending`).
- **The F9 window stays open through an update:** an update that reloads the window module (or any reload of it) used
  to close the window, and its message with it; it now reopens on the same tab, so the update's result shows.

## 1.1.0

- **A support file for bug reports:** open `http://127.0.0.1:8765/support` (or F9 → **Support file**) and LunarLink
  saves `BepInEx/lunarlink-support-<date-time>.zip` with what's needed to find a problem: what failed, the errors from
  the BepInEx, game and LunarLink logs of this run and the previous one (a crash's log survives the restart), Unity's
  crash reports, the plugins, DLLs and settings. Names, Steam ids, the user name, LAN addresses and the access key are
  blanked out. It works at the main menu and while the game is frozen. The bug report form asks for it.

## 1.0.0

- **An MCP server** (`mcp/`), so an AI assistant (Claude Desktop, Claude Code, any MCP client) can use the running
  game: its tools are made from `GET /help?details=1`, plugins' endpoints included; reads only unless started with
  `--allow-writes`. Every tool can cut its answer down (`fields`, `where`, `limit`, `count`), and
  `lunarlink_request` calls any endpoint by path. Runs outside the game with `uvx`; see `mcp/README.md` and the wiki's MCP page.
- **Check for updates at game start** (F9 → UPDATES, a checkbox; `POST /update/modify?auto=1`): off by default, so
  checking stays manual; ticked, LunarLink checks once by itself at every game start.
- **The UPDATES tab shows when something has an update:** an orange count on its name (**UPDATES ●2**) for the release,
  modules and plugins with a newer version.
- **A cleaner F9 window**, in the style of Quantum Conveyors' menus: a cream window with rounded rows, buttons and tab
  pills, a soft gradient on each row (orange for what's running or has an update), a centred title, and a "?" whose
  tooltip says what each tab is for.

## 0.5.0

- **Less in the core:** the normal BepInEx plugins' management (listing, on/off for the next start, its checks and the
  `/plugins/*` endpoints) moved into the `LunarLink.Window` module, so it changes without a core update. `/plugins/*`
  answers once a moon is loaded, like the other module endpoints.

## 0.4.1

- **F9's BEPINEX PLUGINS tab no longer loads a plugin twice:** turning on a normal plugin that already runs hot from
  `BepInEx/hot/` is refused, with what to do instead (`/plugins/enable` refuses it too).
- A plugin turned on by mistake can be turned back off before the restart (a **Disable** button on "on from next start").

## 0.4.0

- **Plugins can update themselves.** A plugin, hot or normal, answers `string UpdateFeed()` with `github:owner/repo` or
  any public HTTPS address of an update file (version, notes, and each file with its SHA-256). F9's UPDATES tab shows
  it, and **Update** installs only its own DLLs: a hot plugin reloads in place, a normal one after a restart. Also
  `POST /update/apply?plugin=<name>`.
- A module built for a newer core now says **needs a newer core**, and that it comes with the next LunarLink release.
- **The updater is a module**, `LunarLink.Update`, so fixes to it come through the module channel like any module's;
  it can update itself. The core keeps only the step that finishes a core update at game start. With the module off,
  F9's UPDATES tab says so.

## 0.3.0

- **Module channel.** Every push to main is built on GitHub and the modules that changed are published to the
  `modules` prerelease. F9's UPDATES tab lists each module: a newer build gets an **Update** button and reloads in place,
  no release or restart needed. A build made for a newer core waits for the next release.
- Updates have their own tab in F9, UPDATES.
- After a core update and the restart it needs, the game checks once more, so newer module builds are offered.
- LunarLink no longer builds against MoonFramework (Perf's power fix finds it by name).
- `manifest.json` records each module's source fingerprint.

## 0.2.0

- **Updates from the game.** F9 checks GitHub for LunarLink's latest release (only when you press
  Check for updates) and installs it: the download is checked against GitHub's SHA-256; if only the modules changed
  they reload in place, and if the core changed it's put in place for the next start (restart to finish). Also
  `POST /update/check`, `GET /update` and `POST /update/apply`.
- Releases carry `manifest.json` (the version and a fingerprint of the core) so the updater knows whether a restart is
  needed.

## 0.1.0

The first release.

- **Hot plugins.** DLLs in `BepInEx/hot/` load into their own collectible load context and reload in place while the
  game runs: the old copy's `Stop()` hands its state to the new copy's `Start(state)`, leftover Harmony patches and
  endpoints are taken out, and old copies are freed. Turning one off unloads it from memory. A plugin needs no
  reference to LunarLink and still ships as a normal BepInEx plugin (`samples/HelloHot`).
- **LunarLink's own features are hot modules:** `LunarLink.Api` (the endpoints), `LunarLink.Perf` (measuring and fps
  fixes) and `LunarLink.Window` (the F9 window). They're on unless you turn them off.
- **The F9 window:** hot plugins (enable, disable, reload, reload changed, rescan), normal BepInEx plugins (on or off
  for the next start, with what needs them) and every endpoint.
- **The HTTP gateway** on `127.0.0.1:8765`: 69 endpoints for the world, bases, building, the player and camera, the
  game, hosting, the game's code and performance, each described with parameters, an example and a real answer
  (`/help.html`, `docs/api.html`). Plugins, hot or normal, can add their own with `HandleHttp`.
- **Safe by default:** this machine only unless you set an address and an access key; nothing changes the game without
  the `allow-writes` file.
- **Freeze log:** `requests.log` records every request, and a watcher writes `MAIN THREAD STUCK` with the last 400
  steps when a frame takes over 3 s.
