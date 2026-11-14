# Hot reload: design plan

**Status:** every phase done (2026-11-13): 0, 1, 1b, 2 (2b, 2c, 2d), 3, 4 (Quantum Conveyors) and 5 (Crew Tools), plus enable/disable for normal BepInEx plugins: the loader, `hot.json`, `/hot/*`, `Bridges.cs`, the F9 window and the core plus hot modules with the new URL pattern (since 2026-11-13 merged into LunarLink.Api and LunarLink.Perf). (`samples/HotSpike`, the phase 0 test plugin, was removed afterwards.) The rest is still a plan.

## Goal

Change a plugin, build it, and have the running game use the new code without a restart. That covers LunarLink's
own endpoints and fixes, and plugins written to a few rules. The same plugin DLL must still ship as a normal BepInEx
plugin that does not need LunarLink.

## What it looks like

```
BepInEx/plugins/LunarLink/LunarLink.dll   core: fixed, loads at game start like any plugin
BepInEx/hot/LunarLink.Api.dll               LunarLink's endpoints (areas World, Base, Build, Player, Game, Host, Dev)
BepInEx/hot/LunarLink.Perf.dll              measuring and the fps fixes (patches the game)
BepInEx/hot/<YourPlugin>.dll                  your plugins while you work on them: hot
```

The hot folder is **`BepInEx/hot/`, not under `plugins/`**. BepInEx loads every DLL anywhere under `plugins/` at
start, so a hot DLL there would run twice: once from BepInEx and once from the loader.

| Part | Holds | Reloads? |
|---|---|---|
| **Core** (`src/LunarLink`) | The TCP server, `Pump` (the one MonoBehaviour), the request queue, the journal and the stuck watcher, the `allow-writes` check, the hot loader, `/status`, `/hot/*` | No: changing it needs a restart, so it stays small |
| **Modules** (`src/LunarLink.<Module>`) | Every other endpoint, plus the fixes: PowerFix, AudioFix, DustMap, Lobby, Vehicles, Perf, EngineTimers and DevCode. See [Endpoint modules](#endpoint-modules) | Yes, each on its own |
| **Your plugins** | Anything written to the rules below | Yes |

The URLs get one consistent pattern (below). Every client is moved to the new names in the same change.

## The hot plugin contract

The contract is matched by **method name**, so a plugin needs no reference to LunarLink. The loader looks for one
public class with a public parameterless constructor and these methods:

| Method | Required | When it is called |
|---|---|---|
| `void Start(string state)` | yes | After loading. `state` is what the previous copy's `Stop` returned (`null` on a first load) |
| `string Stop()` | yes | Before unloading. Undo everything, and return any state worth keeping as a string (JSON) |
| `void Update()`, `void LateUpdate()` | no | Every frame, from the core's `Pump`. (No `OnGUI`: Unity calls it several times a frame, and windows use Unity's UI parts instead) |
| `string HandleHttp(string method, string path, NameValueCollection query, string body)` | no | For each request no earlier handler took. Return JSON, or `null` for "not mine" |
| `static void Boot()` | no | Once, at game start, from the first copy loaded. This is the only place to register MoonFramework buildings and cargo (see below) |

State goes across as a string because each copy has its own versions of the plugin's types. Only types that both
copies share can pass between them: .NET's own types, the game's, Unity's and MoonFramework's.

**The rules for a hot plugin:**

1. **No custom Il2Cpp types.** Don't call `ClassInjector`, and don't define your own MonoBehaviour. A registered
   type can never be unregistered, so a second copy would clash with the first. Use `Update()` and the other frame
   methods instead. Adding Unity's own components (Canvas, Text, Button) is fine.
   - For a component on a particular object (collisions, triggers, `OnDestroy`, pointer clicks, coroutines), the core
     has its own ready-registered component, **`HotHook`**. You add it to any GameObject and give it callbacks. The
     core points those callbacks at the new copy on each reload, and removes them when the plugin stops.
   - The only thing still out of reach is a type the game itself creates or calls through its own classes: a subclass
     of a game class, or an Il2Cpp interface the game calls. None of the plugins in use here (QC, Crew Tools,
     MoonFramework, Moon Performance, Zero-G) does that.
2. **Use one Harmony id**: the plugin's name, or ids that start with it. Call `UnpatchSelf()` for each in `Stop()`.
3. **`Stop()` undoes everything `Start()` did**:
   - destroys the GameObjects it made;
   - removes UI listeners (`onClick.AddListener` points the game at the old copy's code);
   - ends any threads, tasks and timers;
   - unhooks every event, MoonFramework's `OnReady`, `WorldLoaded` and `WorldUnloaded` included.
4. **Register buildings and cargo only in `Boot()`.** MoonFramework accepts them only before its build menu is set up,
   and it can't unregister them.

**Release build.** The plugin keeps a thin `[BepInPlugin]` class. Its `Load()` calls `Boot()` and `Start(null)`, then
adds a small MonoBehaviour of its own that calls `Update` and `LateUpdate`. That MonoBehaviour is registered
only in the release build: in hot mode the loader never runs this class. We'd supply this as a source file to copy
into each plugin (`HotHost.cs`), not as a DLL. One DLL works both ways: `BepInEx/hot/` while you develop,
`BepInEx/plugins/` to ship. Never put it in both.

**On this machine, our plugins always run hot.** QC, Crew Tools and LunarLink's modules live in `BepInEx/hot/` all
the time, not only while being worked on. `plugins/` holds the core and other people's plugins. The release form
(through `HotHost`) is only for shipping, and it's tested once before each release.

What running hot all the time depends on:
- **The core must load.** If it fails, no hot plugin loads.
- **`Boot()` timing.** The core calls each hot plugin's `Boot()` from its own `Load()`. That has to happen before
  MoonFramework sets up its build menu. Phase 0 checks this.
- **No BepInEx dependencies.** BepInEx's `[BepInDependency]` ordering doesn't apply to hot plugins, and a normal plugin
  can't find a hot one through BepInEx's plugin list. None of ours needs either.

## Endpoint modules

LunarLink's endpoints are split into hot modules. Each module reloads, or can be turned off, on its own: a bad
build in `Build` turns only `Build` off, and turning `Perf` off removes every timer it put in.

| Module | Holds | What it's for |
|---|---|---|
| Core (fixed) | `/status`, `/hot/*` | Is the game up, and managing the hot plugins |
| `World` | structures, belts, canisters, stations, terrain, kinds, kit, specs, sun, moon, dust | What's in the world |
| `Base` | production, catchers, power, robots, `/base/health` | Is a base working |
| `Build` | the `create`, `check` and `remove` of structures and belts; the build tool | Changing the world |
| `Player` | player, camera, message, clean | The player and the screen |
| `Game` | money, save, threads | The session itself |
| `Host` | lobby, vehicles | Crew games |
| `Perf` | `/perf`, timers, census, and the fixes | Measuring and fps fixes |
| `Dev` | `/dev/*` | Reading the game's code |
| QC itself | `/quantum/*` | Its channels |

The routes for one thing can come from two modules, for example `GET /structures` from `World` and
`POST /structures/create` from `Build`. The core routes by full path, so that's fine.

**Merged 2026-11-13:** World, Base, Build, Player, Game, Host and Dev are areas of one module, `LunarLink.Api` (one
DLL, one row, about 20 ms to reload). Perf stays its own module because it patches the game: reloading the API never
takes the fps fixes out, and turning Perf off removes every timer and fix without losing the API.

## URL pattern

Each thing gets the same verbs:

- `GET /thing` lists it (read-only).
- `POST /thing/check` asks the game "would this work?" and never changes anything.
- `POST /thing/create`, `/thing/modify` and `/thing/remove` change it.

**Every write is POST**, and a write sent as GET is refused. Writes still need `allow-writes`.

| Thing | List | Check | Create | Modify | Remove | Replaces |
|---|---|---|---|---|---|---|
| structures | `GET /structures?x=&y=&z=&r=&kind=&since=` | ✓ | ✓ (`anywhere=1` once researched) | — | ✓ `ids`, `refund=1` (default) or `0`, `robot=1` | `/depots`, `/build/structure`, `/check/structure`, `/remove/structure`, `/remove/now`, `/remove/batch` |
| belts | `GET /belts` | ✓ | ✓ | — | ✓ `ids` (the game's undo: works from anywhere and refunds) | `/build/belt`, `/check/belt`, `/remove/belt`, `/belt/undo`, `/belt/canremove` |
| robots | `GET /robots` | — | — | — | — | `/robots/map`. Robots are structures (kind 99): placed with `/structures/create`, removed with `/structures/remove?robot=1` (one way per job) |
| canisters | `GET /canisters?x=&y=&z=&radius=` | — | — | — | ✓ | `/remove/canister` |
| vehicles | `GET /vehicles` | — | — | — | ✓ `refund` | `/vehicles/remove` |
| player | `GET /player` | — | — | ✓ `x,y,z` (move) | — | `/player/teleport` |
| camera | `GET /camera` | — | — | ✓ position, target, fov, hide | ✓ (hand the camera back) | `/camera`, `/camera?off=1` |
| lobby | `GET /lobby` | — | — | ✓ `name`, or `key` and `value` | — | `/lobby/data`, `/lobby/name`, `/lobby/set` |
| fixes | `GET /perf/fixes` | — | — | ✓ `name`, `on`, settings | — | `/power/fix`, `/audio/far` |
| Quantum channels (QC) | `GET /quantum/channels` | — | ✓ | ✓ `name` (rename), `adapter` (assign or unassign) | ✓ | `/quantum/status`, `/quantum/create`, `/quantum/assign`, `/quantum/unassign`, `/quantum/rename`, `/quantum/delete` |

**One way per job:**
- **Structures:** `remove` works from anywhere and pays the refund by default. Callers still remove one at a time with a
  0.25 s gap. The player-must-be-close removal is gone.
- **Belts:** the game's undo is the only removal.

**Actions** (POST; they don't fit the pattern):
- `/builder/aim`, `/builder/place` and `/builder/stop` drive the real build tool (`GET /builder` reads its state);
- `/clean`, `/message` and `/game/save`;
- `/quantum/convert`, in QC.

**Read-only reports** (GET):
- `/base/health`, `/production`, `/catchers`, `/power`;
- `/stations` (with `/stations/shape` and `/stations/footprint`), `/terrain`, `/kinds`, `/kit`, `/specs`, `/sun`, `/moon`
  (was `/stat/moon`), `/dust/regions`, `/dust/geometry`;
- `/game/money`, `/debug/threads`, `/perf`, `/perf/methods`, `/census`, `/dev/*`.
- `/perf/time` puts a timer in, so it's POST.

**Moving the clients.** `game.py` and every script switch to the new URLs in the same change, with no old names kept as
aliases. Each changed script is checked on a test base, never a built one.

**Deleted during the split:**
- QuantumFix and QuantumRender, along with `/quantum/fix` and `/quantum/render`;
- `/world/throttle` and `/render/gpu`. Both were measured and neither helped;
- `/dust/complete`, unless a client turns out to use it.

`quantum_every.txt` in the game's LunarLink folder becomes a leftover. Ask before removing it.

### New endpoints

| Endpoint | Module | Returns or does |
|---|---|---|
| `GET /base/health?x=&z=&r=` | Base | Within the circle: machines waiting for input or blocked on output (with the reason); full catchers and catchers with dead outlets; short power or empty batteries; idle robots |
| filters on `GET /structures` | World | `x,z,r` and `kind`, so a client can read one unit without fetching every structure on the map |
| `GET /game/money` | Game | The team's money |
| `POST /game/save` | Game | Save now. **Research first:** find the game's save call and make sure it's safe to call mid-session |
| `GET /debug/threads` | Game | The managed threads that are alive: name, background or not, and their owner if it can be found. For the exit freeze |
| `POST /structures/create?anywhere=1` | Build | Build without the 22 m limit, the way removal already works. **Research first** |
| `GET /quantum/flow` | QC (`HandleHttp`) | Per channel: capsules moved in the last few minutes, and inputs that got nothing |

## Plugins serve their own endpoints

A plugin that has `HandleHttp` gets its requests routed to it by the core. This works whether the plugin is loaded hot
or is a normal BepInEx plugin in `plugins/`:

- The core finds normal plugins by checking BepInEx's loaded plugins for a public `HandleHttp` method, matched by name
  like the rest of the contract.
- Nothing ties the plugin to LunarLink. Without LunarLink, nothing ever calls that method.

**Quantum Conveyors serves `/quantum/*` itself**, in the pattern above. It knows its channels, and it can't go out of
step with them the way outside reflection can. LunarLink's own `/quantum/*` code is deleted.

## The loader

**Loading:**
- Each copy is read from bytes, so the file isn't locked. Its `.pdb` is read too, so errors show line numbers.
- Each copy goes into its own `AssemblyLoadContext`. Anything the context doesn't hold falls through to the shared
  default context: BepInEx, Harmony, Il2CppInterop, the game, Unity and MoonFramework. So a hot plugin uses the same
  MoonFramework instance and the same game objects as everything else.
- Contexts are collectible, so an unloaded copy is freed once nothing points at it. Harmony and Il2CppInterop keep
  pointers in two caches; `Bridges.cs` clears the dead ones (see risk 1).

**Reloading.** All of this runs on the main thread, queued through `Pump`:
1. Call the old copy's `Stop()` and keep the state it returns.
2. **Check for leftovers.** Any Harmony patch still owned by the plugin's id is removed by force, and the log and
   `/hot/status` warn about it.
3. Load the new copy and call `Start(state)`.
4. If `Start` throws, the plugin is marked failed and stays off. `/hot/status` shows the error. The core and other
   plugins carry on.

**At run time:**
- An exception from a frame method turns that plugin off and logs it once. Other plugins keep running.
- `HandleHttp` exceptions become `{"error": ...}`.

**At game start** the core loads every **enabled** DLL in `BepInEx/hot/`: first each one's `Boot()`, then its
`Start(null)` once the world is loaded. That's the same point the fixes install today.

## Managing hot plugins

There are three ways in, and all of them do the same thing through the same code in the core:

- the in-game manager window;
- the HTTP API (for scripts and for me);
- `build.sh --hot`, which builds, copies the DLL into `BepInEx/hot/` and calls the API's reload.

There's **no file watcher**: it would add a thread that runs for as long as the game does, and we're already chasing an
exit freeze. Plugins load, reload or stop only when you ask.

**What each plugin can be:**
- **Enabled:** loaded at game start and running.
- **Disabled:** stopped and unloaded, and not loaded at the next start either.
- **Failed:** off, with the error shown.

Enabled or disabled is saved in `BepInEx/hot/hot.json`, so it survives a restart. A DLL that appears in the folder
starts out disabled until you enable it.

### The API

Part of the core, so it works even when a hot plugin is broken. Every change needs `allow-writes`, like other writes.

| Endpoint | Does |
|---|---|
| `GET /hot/list` | Each plugin's name, state (enabled, disabled or failed, with the error), the loaded build's file time and version, whether a newer build is on disk, load count, leftovers from the last reload, and whether it has `Boot()` |
| `POST /hot/reload?name=X` | Stop X and load the build that's on disk now. If X was disabled, this also enables it |
| `POST /hot/reload?all=1` | Reload every enabled plugin that has a newer build on disk |
| `POST /hot/enable?name=X` | Load and start X, and remember it as enabled |
| `POST /hot/disable?name=X` | Stop and unload X, and remember it as disabled |
| `POST /hot/rescan` | Look for DLLs added to or removed from the folder |

### The manager window

An in-game window, opened with a hotkey (F9 by default, set in the core's config). Since 2026-11-13 it is its own hot
module, `LunarLink.Window`, so window changes reload without a restart; its settings stay in the core:

- one row per hot plugin, showing its name, state, the loaded build's time, a "newer build on disk" mark, and the error if it failed;
- buttons on each row: **Enable/Disable** and **Reload**;
- along the top: **Reload changed** and **Rescan**.

It's built from Unity's own UI parts (Canvas, Text, Button), not a custom MonoBehaviour. It started in the core (so it
kept working when a plugin it manages broke); it moved into its own module so it can change without a restart, and a
broken window build is fixed through `/hot/reload`, which the core always answers. The buttons call the same code as the
API.

### API help

Every endpoint describes itself, so the help is never out of date and plugins' endpoints show up beside LunarLink's:

- **LunarLink's endpoints** are registered as a table in phase 2. Each entry has a method, path, parameters, a one-line
  description and its module, replacing today's big `switch`.
- **A plugin's own endpoints** come from an optional `string[] HttpHelp()` on its class, matched by name like the rest
  of the contract. Each line reads `"GET /quantum/flow?minutes= | Per channel: capsules moved, and inputs that got
  nothing"`. Normal BepInEx plugins with `HandleHttp` can offer it too.
- **`GET /help`** returns them all, grouped by module or plugin. `GET /help?path=/structures` gives one in full.
- **The window gets two tabs:** *Plugins* (the list above) and *API* (every endpoint by module and plugin, with a
  search box). An endpoint whose plugin is disabled isn't shown.
- **The README's endpoint tables** are generated from `/help`, so they match the code.

**Plugins that register buildings.** For a plugin with `Boot()`:
- **Disable** stops its logic, but its buildings stay registered until a restart. They go idle: they accept and send
  nothing.
- **Enable**, for a plugin that wasn't loaded at game start, can't register its buildings. The window and `/hot/list`
  show "restart to register buildings".

## MoonFramework buildings in a hot plugin (QC's case)

MoonFramework keeps the machine object it made for each placed building for as long as the game runs, so a reload
can't swap those objects. Instead:

- `Boot()`, in the first copy, registers the building with a factory that makes a **shell** machine.
- The shell forwards every call (Tick, Accept and the rest) to delegates that the current copy provides. Their
  parameters are only shared types: MoonFramework's, the game's and .NET's.
- On each reload, the core points the shells at the new copy's delegates.

**What reloads:** what the building does. **What needs a restart:** what the building is, meaning its size, model,
ports, price and name.

This is phase 4's design work and isn't needed for phases 0 to 3.

## Phases

| Phase | Work | Done when |
|---|---|---|
| 0. Spike **(done 2026-11-13)** | A throwaway hot plugin with one Harmony patch, an `Update` counter and an endpoint. Reload it 50 times | Patches are gone after each reload; the old copy is either unloaded or its leak is measured; no stutter beyond the reload frame, checked with engine counters only; the game exits cleanly; a `Boot()` called from the core's `Load()` comes before MoonFramework's build menu is set up |
| 1. Core and loader **(done 2026-11-13)** | The loader and the `/hot/*` API in the core; `hot.json`; `Pump` drives the hot plugins; `build.sh --hot` | The spike plugin is enabled, disabled and reloaded through the API, and its state survives a restart |
| 1b. Manager window **(done 2026-11-13)** | The F9 window in the core | Every action works from the window, and a failed plugin shows its error |
| 2. Split LunarLink **(done 2026-11-13)** | Everything but the core moves into the modules, with `Stop()` undoing each fix's patches. `Pump.Update`'s fix installs (PowerFix, AudioFix) move to the modules' `Update`. The new URL pattern goes in, and `game.py` and the scripts move to it | Every script works on a test base; each module reloads 20 times with no patches left over |
| 2b. QC endpoints **(done 2026-11-13, QC 2.1.0 on its moongateway-endpoints branch; LunarLink.Quantum removed)** | QC gets `HandleHttp` for `/quantum/channels` (list, create, modify, remove), `/quantum/convert` and the new `/quantum/flow`. LunarLink's `/quantum` code is deleted. Ships as QC 2.1 | The hookup and teardown scripts work against QC loaded as a normal plugin |
| 2c. New endpoints **(done 2026-11-13: /base/health, /game/money, /debug/threads; save and build-anywhere still need research)** | `/base/health`, the `/structures` filters, `/game/money`, `/debug/threads`; then `/game/save` and building from anywhere once their research is done | Each one checked on a test base, never a built one |
| 2d. API help **(done 2026-11-13: /help, /help.html, HttpHelp, the API tab, tools/endpoints.py)** | `GET /help` from the route table and plugins' `HttpHelp()`; the window's API tab; the README tables generated from it | Every endpoint, LunarLink's and QC's, shows in `/help` and the API tab |
| 3. Template and docs **(done 2026-11-13: samples/HelloHot with HotHost.cs, docs/HOT_PLUGINS.md)** | `HotHost.cs`, a sample plugin, and `docs/HOT_PLUGINS.md` (rules, build, release). The README lists the endpoints by module and pattern | The sample works both ways: from `BepInEx/hot/` and as a normal plugin |
| 4. Quantum Conveyors hot **(done 2026-11-13, QC branch moongateway-endpoints: machines swapped, not shelled - MoonFramework re-makes them from their saved data)** | In its own repo, as its own decision: shells plus `Boot()`. The public release must behave the same | QC's logic reloads; the release DLL needs no LunarLink |
| 5. Crew Tools **(done 2026-11-13, Crew Tools 1.1.0)** | Moved onto the contract | Reloads cleanly |

## Risks and open questions

1. **Harmony and Il2CppInterop with collectible contexts: solved in phase 0.** Old copies stayed in memory because of
   two static caches that never let go: Il2CppInterop's `Il2CppDetourMethodPatcher.DelegateCache` (one bridge per patch
   and unpatch) and Harmony's `DelegateTypeFactory.TypeCache` (keyed by each bridge's method). `Bridges.cs` frees the
   dead entries after each reload or unload. A bridge is freed only if it was already dead at the previous clean-up.
   Measured: after 200 reloads, 1 old copy alive (the newest, waiting its turn); 412 bridges and 412 type-cache
   entries freed.
2. **Il2Cpp delegates.** Unity listeners, coroutines and `Il2CppSystem` actions handed to the game keep pointing at the
   old copy's code. Rule 3 covers this; the leftovers check can't see it, so it relies on the plugin.
3. **Exit freeze.** The loader adds no threads. Phase 0 includes exiting the game after reloads, to make sure it adds
   no new hang.
4. **QuantumFix and QuantumRender: decided, delete them.** They only patch Quantum Conveyors 1.x and Quantum Fast;
   with QC 2.x they install nothing. They go during the split, along with `/quantum/fix` and `/quantum/render`.
5. **The core can't reload.** Server, queue or loader changes still need a restart. That's why the core is kept to
   what phase 1 lists.
