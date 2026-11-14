# Plugin Examples

Working code for hot plugins. The full example lives in the repo as
[`samples/HelloHot`](https://github.com/swheettaos/lunarlink/tree/main/samples/HelloHot); copy that folder to start a
plugin (see [[Writing a Hot Plugin]]).

## HelloHot, the whole thing

`Main.cs` is the plugin: a Harmony patch it takes out again, a frame counter, an endpoint with its help line, and a
number handed from each copy to the next.

```csharp
<!-- include: samples/HelloHot/Main.cs -->
```

`HotHost.cs` makes the same DLL a normal BepInEx plugin, for players who don't run LunarLink. Only its three names
change from plugin to plugin.

```csharp
<!-- include: samples/HelloHot/HotHost.cs -->
```

Build, load and try it, with the game running:

```bash
./build.sh --hot HelloHot                              # build, copy into BepInEx/hot/, reload
curl -s -X POST "127.0.0.1:8765/hot/enable?name=HelloHot"   # the first time only: a new DLL starts off
curl -s 127.0.0.1:8765/hello                           # {"ok":true,"starts":1,"frames":...,"productionSteps":...}
./build.sh --hot HelloHot                              # change something, run it again: starts is now 2
```

## WorldHot: a plugin that sets up the world

[`samples/WorldHot`](https://github.com/swheettaos/lunarlink/tree/main/samples/WorldHot) shows what a plugin like
Mars Sweepers needs to reload mid-game: `StartEarly` (it starts before any world), `WorldLoaded()` and
`WorldUnloaded()` (a marker object placed in each world and removed when it goes, also right after a mid-game reload),
its own Unity type (`WorldMarker`, renamed `WorldMarker_hot2` in the next copy) and its counters handed over through
`Stop()`. `GET /worldhot` reports it all. Its `HotHost` calls the world events itself when it runs as a normal plugin.

```csharp
<!-- include: samples/WorldHot/Main.cs -->
```

```csharp
<!-- include: samples/WorldHot/HotHost.cs -->
```

## Recipes

### Hand over structured state

`Stop()` returns a string; JSON carries anything structured. Both copies have their own types, so only plain data
crosses.

```csharp
using System.Text.Json;

sealed class Saved { public int Starts { get; set; } public List<int> Watched { get; set; } = new(); }
Saved saved = new();

public void Start(string state)
{
    if (state != null) saved = JsonSerializer.Deserialize<Saved>(state) ?? new Saved();
    saved.Starts++;
}

public string Stop() => JsonSerializer.Serialize(saved);
```

### Several endpoints, the LunarLink way

Reads are `GET`, changes `POST`; return `null` for any path that isn't yours so the next plugin gets it. LunarLink passes
a `POST` on only when `allow-writes` exists, so you needn't check it.

```csharp
public string HandleHttp(string method, string path, NameValueCollection query, string body)
{
    if (!path.StartsWith("/notes")) return null;
    switch (path)
    {
        case "/notes":
            return method == "GET" ? JsonSerializer.Serialize(notes) : "{\"error\":\"use GET\"}";
        case "/notes/create":
            if (method != "POST") return "{\"error\":\"use POST\"}";
            notes.Add(query["text"] ?? "");
            return "{\"ok\":true,\"count\":" + notes.Count + "}";
    }
    return "{\"error\":\"unknown path " + path + "\"}";
}

public string[] HttpHelp() => new[]
{
    "GET /notes | Every note",
    "POST /notes/create?text= | Add a note",
};
```

### A window of your own

Use Unity's own UI parts on a canvas you make, destroy it in `Stop()`, and draw it from `Update()` (no MonoBehaviour of
your own while hot).

```csharp
GameObject canvas;

void Open()
{
    canvas = new GameObject("MyPlugin window"); UnityEngine.Object.DontDestroyOnLoad(canvas);
    var c = canvas.AddComponent<Canvas>(); c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = 30000;
    canvas.AddComponent<CanvasScaler>();
    canvas.AddComponent<GraphicRaycaster>();
    // ... add Image / Text children ...
}

public string Stop()
{
    if (canvas != null) UnityEngine.Object.Destroy(canvas);    // nothing of the old copy stays on screen
    return null;
}
```

Buttons: don't use `onClick.AddListener` (the game would keep calling the old copy after a reload); raycast the mouse
press in `Update()` and run your own action, as LunarLink's window does.

### Patch a game method and take it out again

```csharp
Harmony harmony;

public void Start(string state)
{
    harmony = new Harmony("MyPlugin");                         // one id for all of this plugin's patches
    harmony.Patch(AccessTools.Method(typeof(MoonCleaners.MoonWorld), "StepProduction", new[] { typeof(float) }),
                  postfix: new HarmonyMethod(typeof(Main), nameof(After)));
}

public string Stop() { harmony?.UnpatchSelf(); return null; }

static void After() { /* runs after every production step */ }
```

If a patch is still in after `Stop()`, LunarLink takes it out and lists it as a leftover in `/hot/list`.

### Update itself

One method; LunarLink does the rest (check, SHA-256, install, reload). On `Main`, and the same on `HotHost` so it
works when installed normally:

```csharp
// your GitHub repo's latest release (its .zip)
public string UpdateFeed() => "github:you/yourplugin";

// or an update file on any public HTTPS host
public string UpdateFeed() => "https://example.com/yourplugin/update.json";
```

`update.json` names the version, notes, and the file with its SHA-256 (or several files, for a plugin made of
modules): see [[Writing a Hot Plugin]] ("Letting it update itself").

### Buildings

A plugin that adds MoonFramework buildings registers them in `static void Boot()` and swaps their machines on a
reload: see [[Plugins with Buildings]].
