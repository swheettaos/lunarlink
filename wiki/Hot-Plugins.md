# Hot Plugins

A hot plugin is a DLL that LunarLink loads from **`BepInEx/hot/`** and can reload while the game runs. LunarLink's own
endpoints, its fps fixes and the F9 window are hot plugins themselves (`LunarLink.Api`, `LunarLink.Perf`,
`LunarLink.Window`), and any plugin written to a few rules can be one (see [[Writing a Hot Plugin]]).

LunarLink is optional for the people who use your plugin: a hot plugin is still a normal BepInEx plugin, and runs
without LunarLink from `BepInEx/plugins/`. It's hot only when it's in `BepInEx/hot/` with LunarLink installed.

## Why a separate folder

(Which of your plugins go where, and how to move one: [[Installing Plugins]].)

BepInEx loads every DLL under `BepInEx/plugins/` at game start, once, for good. A hot DLL there would run twice (once
from BepInEx, once from LunarLink) and could never be reloaded. So hot plugins live in `BepInEx/hot/`, and the same DLL
goes in `BepInEx/plugins/` only when it's shipped to people who run it the normal way. LunarLink refuses a hot copy of a
plugin BepInEx already loaded, so it can't run twice by mistake.

## On and off

`BepInEx/hot/hot.json` remembers which hot plugins are on (`enabled`) and which you turned off (`disabled`). A DLL new
to the folder stays **off** until you turn it on, so nothing runs just because a file appeared.

| State | Means |
|---|---|
| `running` | Loaded and started |
| `loaded` | Loaded, waiting for a moon to load before it starts |
| `disabled` | Off: not loaded, now or at the next start. Turning a running plugin off **unloads it from memory**: its code, patches and endpoints are gone, no restart needed |
| `failed` | It didn't load or start; the error says why. Nothing of it runs |

```mermaid
stateDiagram-v2
  [*] --> disabled: a new DLL appears
  [*] --> loaded: game start, it was on
  disabled --> loaded: Enable, before a moon is loaded
  disabled --> running: Enable
  loaded --> running: a moon has loaded
  running --> running: Reload
  running --> disabled: Disable
  loaded --> disabled: Disable
  disabled --> failed: it didn't load
  running --> failed: Start or Update threw
  failed --> running: fix it, then Reload
  failed --> disabled: Disable
```

## Reloading

From the game: [[The F9 Window]] (**Reload** on a row, **Reload changed** for everything with a newer build on disk).
From a script:

| Request | Does |
|---|---|
| `GET /hot/list[?gc=1]` | Every hot plugin: state, the loaded build's time, newer build on disk, error, leftovers, old copies still in memory |
| `POST /hot/reload?name=` | Stop it, load the build on disk now, start it with what the old copy handed over (and turn it on) |
| `POST /hot/reload?all=1` | Reload every plugin that has a newer build on disk |
| `POST /hot/enable?name=` · `/hot/disable?name=` | Turn it on (load and start) or off (stop and unload), and remember it |
| `POST /hot/rescan` | Pick up DLLs added to or removed from the folder |

The usual loop while writing one:

```mermaid
flowchart LR
  edit["Edit the code"] --> build["./build.sh --hot MyPlugin<br/>(or dotnet build + copy)"]
  build --> reload["LunarLink reloads it<br/>Stop → load → Start(state)"]
  reload --> try["Try it in the game<br/>or with curl"]
  try --> edit
```

From the repo, with the game running: `./build.sh --hot <Name>` builds a module or a sample, copies it in and reloads it.

## What a reload does

1. The old copy's `Stop()` runs: it undoes what it did and hands over what the next copy should keep.
2. LunarLink checks for anything left behind: a Harmony patch still in is taken out and reported as a leftover; the
   copy's endpoints are removed.
3. The new copy loads (into its own load context, from memory, so the file isn't locked) and its `Start(state)` runs.
4. If `Start` throws, the plugin is unloaded again and shows as failed; everything else carries on.

```mermaid
sequenceDiagram
  participant You as F9 / POST /hot/reload / build.sh --hot
  participant L as Hot loader
  participant Old as Old copy
  participant New as New copy
  You->>L: reload MyPlugin
  L->>Old: Stop()
  Old-->>L: what to keep (a string)
  L->>L: take out leftover Harmony patches and its endpoints
  L->>L: unload the old context
  L->>New: load the DLL on disk into a new context
  L->>New: Start(what the old copy kept)
  alt Start throws
    L->>L: unload it again: "failed"
  else
    Note over New: running: Update and LateUpdate every frame
  end
  L->>L: drop dead bridges, so older copies can be freed
```

A reload is usually a few milliseconds; a plugin with many game hooks takes longer (tens to a hundred ms), one short
hitch.

## Memory

Each copy is loaded so it can be freed. Two caches inside BepInEx's Harmony support would keep every old copy alive
(one bridge per patch change, and a delegate-type cache keyed by it), so after each reload LunarLink drops the entries
of patches that are gone - only ones already dead at the previous reload, so nothing the game could still be calling.
`GET /hot/list?gc=1` shows how many old copies are still in memory (normally 0 or 1).

## Updating them

A hot plugin that says where its builds are (`public string UpdateFeed()`: `github:owner/repo` or any public HTTPS
address of an update file) gets a row on F9's UPDATES tab: **Update** downloads the new build, checks its SHA-256,
puts it in `BepInEx/hot/` and reloads it, with no restart. See [[Updates]].

## Safety

A hot plugin runs with the same rights as any BepInEx plugin: there is no sandbox. Only turn on DLLs you trust.
