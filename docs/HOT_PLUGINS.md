# Writing a hot plugin

A hot plugin is a DLL that LunarLink loads from `BepInEx/hot/` and can reload while the game runs: build it, reload
it, and the game uses the new code within a frame. The same DLL still ships as a normal BepInEx plugin that doesn't need
LunarLink. You can write a new one from `samples/HelloHot`, or [convert a plugin you already
have](#converting-an-existing-plugin): it's mostly moving code into `Start`, and writing a `Stop` that undoes it.

LunarLink is optional for your players: the DLL runs as a normal BepInEx plugin without it. In `BepInEx/hot/` with
LunarLink installed, the same DLL reloads in place, and turning it off unloads it from memory with no restart.

## Start from HelloHot

1. Copy `samples/HelloHot` to `samples/<YourName>` (or to its own repo), and rename `HelloHot` everywhere: the folder,
   the `.csproj`, the namespace and the Harmony id in `Main.cs`, and the three names at the top of `HotHost.cs`.
2. Put your work in `Main`: start it in `Start`, undo it in `Stop`, and do per-frame work in `Update`.
3. Build it and load it hot, with the game running:

   ```bash
   ./build.sh --hot <YourName>      # build samples/<YourName>, copy it into BepInEx/hot/ and reload it
   ```

   A DLL new to `BepInEx/hot/` starts off. Turn it on once with F9 (HOT PLUGINS tab, **Enable**) or
   `POST /hot/enable?name=<YourName>`; it then stays on across restarts.
4. Change, `./build.sh --hot <YourName>` again, and the new code runs. `GET /hot/list` (or F9) shows its state, build
   time and any error.

A plugin in another repo builds against the game's DLLs the same way: copy `Directory.Build.props` from here (it holds
the references), build, copy the `.dll` and `.pdb` into `BepInEx/hot/`, and reload with F9 or
`POST /hot/reload?name=<YourName>`.

LunarLink itself doesn't use MoonFramework at compile time, so `Directory.Build.props` doesn't reference it. A plugin
that does (buildings, cargo, crew messages) adds it to its own `.csproj`:
`<Reference Include="$(GameDir)/BepInEx/plugins/MoonFramework.dll" Private="false" />`.

## Converting an existing plugin

A normal BepInEx plugin does its work in `Load()` and never expects to stop. To make it hot, split it in two: a plain
`Main` class that starts and stops, and a thin `BepInPlugin` host that only runs in the normal build.

| In the normal plugin | In the hot plugin |
|---|---|
| The work in `BasePlugin.Load()` | Moves to `Main.Start(string state)`, which runs once a moon has loaded (or at game start, with `StartEarly`) |
| Setting up each world as it loads (patching world loading, binding to its objects) | `Main.WorldLoaded()`, which also runs right after `Start` when a world is already loaded; `Main.WorldUnloaded()` lets it go |
| Nothing undoes it | `Main.Stop()` undoes **all** of it, in reverse |
| A MonoBehaviour of your own (`ClassInjector`, `AddComponent<T>()`) for `Update` | `Main.Update()` and `Main.LateUpdate()`: LunarLink calls them every frame. Your own Unity types for objects in the world are fine too (rule 1) |
| `Harmony.CreateAndPatchAll(...)` | `harmony = new Harmony(Id); harmony.PatchAll(...)` in `Start`, `harmony.UnpatchSelf()` in `Stop` |
| Static fields that live for the whole game | Fields on `Main`; what must survive a reload is returned by `Stop()` as a string (JSON) and read back in `Start(state)` |
| `Log` from `BasePlugin` | `Logger.CreateLogSource(name)` in `Start`, `Logger.Sources.Remove(log)` in `Stop` |
| `Config.Bind(...)` | `new ConfigFile(path, true)` in `Start` (the `BepInPlugin`'s `Config` exists only in the normal build) |
| `Moon.RegisterBuilding(...)` / cargo in `Load()` | `static void Boot()` on `Main` (see rule 4 below) |
| Event subscriptions, UI listeners, GameObjects | Made in `Start`, removed or destroyed in `Stop` |

The steps:

1. **Copy `HotHost.cs`** from `samples/HelloHot` into your project and change the three names at its top. It becomes the
   `BepInPlugin` class: it creates `Main`, calls `Start(null)` (call your `Boot()` just before it, if you have one),
   and drives `Update()` from its own MonoBehaviour - in the normal build only.
2. **Move your plugin class's work into `Main`**, following the table. Remove the old `[BepInPlugin]` attribute, so
   `HotHost` is the only one.
3. **Write `Stop()`** by reading `Start()` from the bottom up: every patch, subscription, object and thread it made gets
   undone. This is the whole job; the [rules](#the-rules) list what's easy to miss.
4. **Build it into `BepInEx/hot/`**, turn it on (F9 or `POST /hot/enable?name=`), and take the normal copy out of
   `BepInEx/plugins/` for now (F9's BEPINEX PLUGINS tab, then restart once).
5. **Reload it ten times** (`POST /hot/reload?name=`) and check `GET /hot/list?gc=1`: no `leftovers`, `oldCopiesAlive`
   0 or 1, and the plugin still behaves. If it doesn't, `Stop()` missed something.

Players keep installing it exactly as before: the same DLL in `BepInEx/plugins/` runs through `HotHost`.

How big the change is depends on how much the plugin hooks into: a plugin that patches a few methods converts in an hour.
One with hundreds of placed MoonFramework buildings also needs its machines handed over on a reload (see rule 4); done
that way, a plugin with 400+ placed buildings reloads in about a tenth of a second with every building's state kept.

## The contract

LunarLink finds one public class with a parameterless constructor and these methods, by name. The plugin needs no
reference to LunarLink.

| Method | Needed | When it's called |
|---|---|---|
| `void Start(string state)` | yes | After loading. `state` is what the previous copy's `Stop` returned (`null` on a first load) |
| `string Stop()` | yes | Before the copy is dropped. Undo everything `Start` did and return what the next copy should keep, as a string (JSON for anything structured) |
| `void Update()`, `void LateUpdate()` | no | Every frame |
| `string HandleHttp(string method, string path, NameValueCollection query, string body)` | no | For each request no LunarLink route answered. Return JSON, or `null` for "not mine" |
| `string[] HttpHelp()` | no | One line per endpoint, `"GET /path?x= \| what it does"`, for `/help`, `/help.html` and the API tab |
| `static void Boot()` | no | Once, at game start, from the first copy. The only place to register MoonFramework buildings and cargo |
| `static bool StartEarly` (property or field, `true`) | no | Not a call: with it, `Start` runs at game start, where BepInEx starts normal plugins, before any world, and again there after a reload at the main menu. For a plugin that has to be in place before a save loads (it patches world loading). `Start` must not assume a world then |
| `void WorldLoaded()` | no | Each time a world has finished loading (after the loading screen), **and right after `Start` when a world is already loaded**: set the world up here, and a copy reloaded mid-game sets it up exactly as a fresh load would |
| `void WorldUnloaded()` | no | Once the world is gone (the main menu): let go of what `WorldLoaded` set up |
| `string UpdateFeed()` | no | When the player checks for updates: where your builds are (see [Letting it update itself](#letting-it-update-itself)) |

State crosses as a string because each copy has its own versions of your types; only .NET's, the game's, Unity's and
MoonFramework's types are shared between copies.

## The rules

1. **Your own Il2Cpp types need the LunarLink.Wrap module.** Il2Cpp never forgets a registered type's name, so each copy
   after the first has its types renamed as it loads (`WorldMarker` → `WorldMarker_hot2`) by the Wrap module (on by
   default); without it, a reload of a plugin with its own types fails. Register them in `Start` only when not yet
   registered (`ClassInjector.IsTypeRegisteredInIl2Cpp<T>()`), and destroy their objects in `Stop()`: anything of them
   still there afterwards is destroyed by LunarLink, and the old types' bridges are freed once Unity has removed the
   objects. Don't look your types up by name string: the renamed ones wouldn't match. For per-frame work `Update()` is
   still simplest.
2. **One Harmony id** (your plugin's name, or ids starting with it), and `UnpatchSelf()` in `Stop()`. LunarLink takes
   out any patch left under that id after `Stop()` and reports it as a leftover.
3. **`Stop()` undoes everything:** destroy the GameObjects you made (and any EventSystem you created), remove UI listeners
   (`onClick.AddListener` points the game at the old copy), end threads and timers, unhook events, MoonFramework's
   included. MoonFramework's crew-message handlers have no remove call: take your own out of its list (by your
   assembly) in `Stop()`.
4. **Buildings and cargo only in `Boot()`.** MoonFramework accepts them only before its build menu is set up and can't
   unregister them. Changing a building's definition (model, ports, price, name) needs a game restart; what its machine
   does can reload: in `Start()`, put your machine factory on the building's spec (`Moon.BuildingOf(id).Spec.Machine`);
   in `Stop()`, save and stop your live machines and give their data back to MoonFramework so the next copy's machines
   start from it.

## Plugins that set up the world

A plugin that changes how a world is built (Mars Sweepers builds a whole planet as a save loads) needs three things to
reload mid-game instead of only at the main menu. [`samples/WorldHot`](https://github.com/swheettaos/lunarlink/tree/main/samples/WorldHot) shows all of them:

1. **`StartEarly`**, so its patches on world loading are in before the first save loads.
2. **Its world set-up in `WorldLoaded()`**, not in a patch's one-off: LunarLink calls it on every load *and* right
   after a reloaded copy's `Start` in a running world, so the new copy binds to the world that's already there (its
   machines, its objects) the same way.
3. **Its live state handed over:** `Stop()` returns what its world objects hold (Mars: its machines' production state)
   and the next `Start(state)` reads it back before `WorldLoaded()` binds to the world.

Plus, as for any hot plugin: `Stop()` unloads what it loaded (an asset bundle: `bundle.Unload(true)`, or the next copy
can't load it), destroys its objects, and takes out its patches.

Two traps seen in this game: Unity's generic `FindObjectOfType<T>()` doesn't exist here (it's stripped: use
`FindObjectsOfType(Il2CppType.Of<T>())`), and a world object exists before the game has finished loading it, so wait
for `WorldLoaded()` rather than polling for the world.

## Endpoints

`HandleHttp` gets every request no LunarLink route answered, in turn with the other plugins; answer the paths you own
(say `/yourname/...`) and return `null` for the rest. The pattern LunarLink uses: `GET /thing` lists it, `POST
/thing/check|create|modify|remove` changes it. A POST reaches a plugin only when the `allow-writes` file exists, so a
plugin needn't check that itself. Describe each endpoint in `HttpHelp()`: it then shows in `/help`, `/help.html` and F9's
API tab.

Installed the normal way, the `BepInPlugin` class (`HotHost`) forwards `HandleHttp` and `HttpHelp`, so LunarLink passes
requests to it just the same.

## Shipping it

Build it once more and give players the same DLL, in `BepInEx/plugins/<YourName>/`. BepInEx loads `HotHost`, which runs
`Main` and drives it every frame; LunarLink isn't needed.

Publish your releases somewhere public (GitHub releases, or any HTTPS host) and answer `UpdateFeed()` with where they
are: then players with LunarLink get them from F9's UPDATES tab, without unzipping anything (see
[Letting it update itself](#letting-it-update-itself)).

On your own machine, never have it in both folders: BepInEx would load the `plugins/` copy at start, and LunarLink
refuses a hot copy of a plugin BepInEx already loaded (it says which to turn off). F9's BEPINEX PLUGINS tab turns the
`plugins/` copy off for the next start.

## Letting it update itself

LunarLink can update your plugin from the game, the same way it updates itself: F9's UPDATES tab shows a row for it,
and **Update** installs the newer version. Running hot, it reloads in place: **no restart, however often it's
updated**. (Only LunarLink's own core ever needs a restart; a plugin installed the normal way in `BepInEx/plugins/`
does too, since BepInEx can't unload it.)
Your plugin only says where its builds are, with one more public method, found by name like the others:

```csharp
public string UpdateFeed() => "github:you/yourplugin";
```

Put it on the class LunarLink starts (`Main`) and, so it works when the plugin is installed normally too, on the
`BepInPlugin` class (`HotHost`). It's optional: without it, nothing changes.

**Where the builds can be: any public HTTPS address.** GitHub isn't required; it's only the shortest way to say it.

| `UpdateFeed()` answers | LunarLink reads |
|---|---|
| `"github:you/yourplugin"` | That repo's **latest GitHub release**: the version is its tag (`v1.2.3`), the notes are its text, the file is its `.zip` (or a `.dll` named like your plugin's). GitHub publishes each file's SHA-256, which is checked |
| `"https://example.com/yourplugin/update.json"` | An **update file you host anywhere public** (your site, GitHub Pages, any static host; not a page behind a login): the version, notes and the file's address and SHA-256 |
| `null` | No updates |

The update file, for a plugin of one DLL (or a zip holding it):

```json
{
  "version": "1.2.3",
  "notes": "Fixes the cap for robots.",
  "url": "https://example.com/yourplugin/YourPlugin-1.2.3.zip",
  "sha256": "9f2c0e...the file's SHA-256, lower case"
}
```

For a plugin split into **modules** (several DLLs that each reload on their own), list each file:

```json
{
  "version": "1.3.0",
  "notes": "New Stats module.",
  "files": [
    { "name": "YourPlugin.dll",       "url": "https://example.com/yourplugin/YourPlugin.dll",       "sha256": "..." },
    { "name": "YourPlugin.Stats.dll", "url": "https://example.com/yourplugin/YourPlugin.Stats.dll", "sha256": "..." }
  ]
}
```

`sha256sum YourPlugin-1.2.3.zip` (Linux) or `Get-FileHash YourPlugin-1.2.3.zip` (PowerShell) gives the SHA-256.

**What LunarLink checks, and what it won't do:**

- It only looks when the player presses **Check for updates** (or `POST /update/check`), or once at game start if they
  ticked that, and installs only when they
  press **Update**. The row shows where the update comes from.
- The version is compared with yours: your `[BepInPlugin]` version (the one in `HotHost`), or the assembly's.
- Every download is HTTPS and must match its SHA-256, or nothing is installed.
- It installs **only your plugin's own DLLs**: your DLL's name, or names starting with `YourPlugin.` (from the zip,
  only the folder holding your DLL). A hot plugin's go into `BepInEx/hot/` and it reloads; a normal plugin's DLL is
  swapped for the next start, as LunarLink swaps its own core.

## When something's wrong

| What you see | What it means |
|---|---|
| `failed` with an error in F9 or `/hot/list` | It didn't load (no class with `Start`/`Stop`, a missing reference) or `Start`/`Update` threw. Nothing of it runs; fix and reload |
| `leftovers` after a reload | A Harmony patch was still in after `Stop()`; LunarLink took it out. Unpatch it in `Stop()` |
| `oldCopiesAlive` keeps growing (`/hot/list?gc=1`) | Something still points at an old copy: a listener, an event, a static in another plugin. `Stop()` should remove it |
| It runs twice | It's in `BepInEx/plugins/` too: turn that copy off |
