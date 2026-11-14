// Hot plugins: DLLs in BepInEx/hot/ loaded by LunarLink, each into its own AssemblyLoadContext, so a new build can
// replace the running one without a game restart. A plugin is found by method names (no reference to LunarLink):
//   public class with a parameterless constructor and   void Start(string state)   string Stop()
//   optional: void Update()  void LateUpdate()  string HandleHttp(string method, string path, NameValueCollection query, string body)
//             static void Boot()   (once, at game start, before MoonFramework's build menu: buildings and cargo go here)
// Which plugins are on is kept in BepInEx/hot/hot.json ({"enabled":[names], "disabled":[names]}): a DLL in neither list
// (new to the folder) stays off until it's enabled - except LunarLink's own modules (LunarLink.*), which are on unless
// they're in "disabled", so an update that brings a new module runs it and a module turned off on purpose stays off.
// optional: string[] HttpHelp()  (one line per endpoint, "GET /path?x= | what it does", for /help and the API tab)
// A DLL without such a class but with a normal BepInEx plugin class runs wrapped, when the LunarLink.Wrap module is on:
// the module rewrites it as it loads and hands back an object with Start and Stop (see IWrapper below and Track.cs).
// Everything here runs on the main thread (Load, Pump's frames, and requests, which Pump answers).
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using HarmonyLib;

namespace LunarLink
{
    static class Hot
    {
        internal sealed class Context : AssemblyLoadContext
        {
            readonly string folder;                                          // the plugin's own folder (hot/<name>/), or null
            public Context(string name, bool collectible, string folder) : base(name, collectible) { this.folder = folder; }   // collectible: an unloaded copy is freed (see Bridges.cs)
            // a DLL in the plugin's own folder loads with it (from memory, so the file can be replaced); everything else
            // comes from the default context
            protected override Assembly Load(AssemblyName name)
            {
                if (folder == null) return null;
                var f = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(f) ? LoadFromStream(new MemoryStream(File.ReadAllBytes(f))) : null;
            }
        }

        internal sealed class Plugin
        {
            public string Name, Error, State;
            public DateTime FileTime;
            public int Loads;
            public bool Running, Booted, HasBoot;
            public bool Enabled
            {
                get => enabled.Contains(Name);
                set { if (value) { enabled.Add(Name); disabled.Remove(Name); } else { enabled.Remove(Name); disabled.Add(Name); } }
            }
            public Context Ctx;
            public Assembly Asm;
            public object Obj;
            public bool Wrapped, WasWrapped;                                  // a normal BepInEx plugin run wrapped (now, or its last copy)
            public bool WrapNative;                                           // native hooks the wrapper can't undo: off waits for a restart
            public bool ReloadAtMenu;                                         // a wrapped plugin's reload asked for in a world: done at the main menu
            public bool LoadsContent;                                         // the wrapper found it loads asset bundles (its own content)
            public bool Early;                                                // a hot plugin with StartEarly: starts at game start, before any world
            public MethodInfo WorldM, WorldGoneM;                             // its optional WorldLoaded() and WorldUnloaded()
            public float WatchUntil; public int WatchErrors;                  // just reloaded: its errors are counted (ReloadWatch)
            public readonly List<string> Warnings = new List<string>();     // what the wrapper found in the DLL, for F9
            public MethodInfo StartM, StopM, UpdateM, LateM, HttpM, HelpM;
            public readonly List<WeakReference> Old = new List<WeakReference>();   // earlier copies' contexts, to see if they unload
            public readonly List<string> Leftovers = new List<string>();
        }

        static readonly Dictionary<string, Plugin> plugins = new Dictionary<string, Plugin>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);    // hot.json; may name DLLs not on disk
        static readonly HashSet<string> disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool started;
        internal static string Dir => Path.Combine(BepInEx.Paths.BepInExRootPath, "hot");
        // A plugin is hot/<name>.dll, or hot/<name>/<name>.dll: a folder named like its DLL, for a plugin that brings
        // files of its own (resources it reads beside its DLL, or DLLs it needs, which load with it: Context.Load).
        internal static string FileOf(string name)
        {
            var flat = Path.Combine(Dir, name + ".dll");
            if (File.Exists(flat)) return flat;
            var inFolder = Path.Combine(Dir, name, name + ".dll");
            return File.Exists(inFolder) ? inFolder : flat;
        }

        // the loaded hot plugins and their objects (the Update module asks them for UpdateFeed())
        internal static List<(string name, object obj)> Loaded() => plugins.Values.Where(p => p.Obj != null).Select(p => (p.Name, p.Obj)).ToList();
        static string SettingsFile => Path.Combine(Dir, "hot.json");

        sealed class Settings
        {
            public List<string> enabled { get; set; } = new List<string>();
            public List<string> disabled { get; set; } = new List<string>();
        }

        // At game start (from LunarLink's Load): load every enabled DLL in the folder and call its Boot().
        internal static void LoadAll()
        {
            HotTrack.InstallPins(new Harmony("LunarLink.pins"));          // so wrapped plugins' Il2Cpp types can be freed
            // wrapped plugins run where BepInEx would have run them: once it has loaded every normal plugin
            BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Finished += () =>
            {
                chainDone = true;
                foreach (var w in plugins.Values.Where(x => (x.Wrapped || x.Early) && !x.Running && x.Error == null).ToList()) Start(w, null);
            };
            if (!Directory.Exists(Dir)) return;
            if (!File.Exists(SettingsFile))
            {
                // first start with enable/disable: every DLL already in the folder was loaded before, so it stays on
                foreach (var name in DllNames()) enabled.Add(name);
                Save();
            }
            else
            {
                var set = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? new Settings();
                foreach (var name in set.enabled) enabled.Add(name);
                foreach (var name in set.disabled ?? new List<string>()) if (!enabled.Contains(name)) disabled.Add(name);
            }
            // LunarLink's own modules are on unless turned off on purpose, so an update that brings a new one (from a
            // release zip or build.sh --install) has it running
            var mine = DllNames().Where(n => n.StartsWith("LunarLink.", StringComparison.OrdinalIgnoreCase) && !enabled.Contains(n) && !disabled.Contains(n)).ToList();
            foreach (var name in mine) enabled.Add(name);
            if (mine.Count > 0) Save();
            Rescan();
            // LunarLink's own modules first: the wrapper module has to be in place before a plugin it wraps loads
            foreach (var p in plugins.Values.Where(p => p.Enabled && File.Exists(FileOf(p.Name))).OrderBy(p => p.Name.StartsWith("LunarLink.", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList())
            {
                try { Load(p); if (p.HasBoot) { Call(p, "Boot"); p.Booted = true; } }
                catch (Exception e) { Fail(p, "load", e); }
            }
        }

        // The wrapper slot, filled by the LunarLink.Wrap module while it runs.
        internal static IWrapper Wrapper;

        static bool chainDone;                                            // BepInEx has loaded its plugins: wrapped ones can run

        static IEnumerable<string> DllNames() => Directory.Exists(Dir)
            ? Directory.GetFiles(Dir, "*.dll").Select(Path.GetFileNameWithoutExtension)
                .Concat(Directory.GetDirectories(Dir).Select(Path.GetFileName).Where(d => File.Exists(Path.Combine(Dir, d, d + ".dll"))))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n) : Enumerable.Empty<string>();

        static void Save()
        {
            var set = new Settings { enabled = enabled.OrderBy(n => n).ToList(), disabled = disabled.OrderBy(n => n).ToList() };
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsFile + ".new", System.Text.Json.JsonSerializer.Serialize(set, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(SettingsFile + ".new", SettingsFile, true);
        }

        // New DLLs in the folder show up (disabled unless hot.json enables them); a plugin whose DLL is gone is stopped
        // and dropped from the list (hot.json keeps it enabled, so it comes back on if the DLL returns).
        static (List<string> added, List<string> removed) Rescan()
        {
            var onDisk = new HashSet<string>(DllNames(), StringComparer.OrdinalIgnoreCase);
            var added = onDisk.Where(n => !plugins.ContainsKey(n)).ToList();
            foreach (var n in added) Get(n);
            var removed = plugins.Keys.Where(n => !onDisk.Contains(n)).ToList();
            foreach (var n in removed) { if (plugins[n].Obj != null) StopAndUnload(plugins[n]); plugins.Remove(n); }
            return (added, removed);
        }

        // Once the world has loaded (the point LunarLink's own fixes go in): Start every loaded plugin.
        internal static void StartAll()
        {
            started = true;
            foreach (var p in plugins.Values) if (p.Obj != null && !p.Running && p.Error == null) Start(p, null);
        }

        internal static void Update() { HotTrack.Frame(); ReloadWaiting(); ReloadWatch.Check(); foreach (var p in plugins.Values) if (p.Running && p.UpdateM != null) Frame(p, p.UpdateM); }
        internal static void LateUpdate() { foreach (var p in plugins.Values) if (p.Running && p.LateM != null) Frame(p, p.LateM); }

        // A request no LunarLink route took: offered to each running plugin with HandleHttp; null means "not mine".
        // /help: each running plugin's HttpHelp() lines ("GET /path?x= | what it does")
        internal static List<(string owner, string[] lines)> Help()
        {
            var list = new List<(string, string[])>();
            foreach (var p in plugins.Values)
            {
                if (!p.Running || p.HelpM == null) continue;
                try { list.Add((p.Name, (string[])p.HelpM.Invoke(p.Obj, null) ?? new string[0])); }
                catch (Exception e) { list.Add((p.Name, new[] { "? | HttpHelp failed: " + (e.InnerException ?? e).Message })); }
            }
            return list;
        }

        internal static string Http(string method, string path, NameValueCollection query, string body)
        {
            foreach (var p in plugins.Values)
            {
                if (!p.Running || p.HttpM == null) continue;
                string r;
                try { r = (string)p.HttpM.Invoke(p.Obj, new object[] { method, path, query, body }); }
                catch (Exception e) { var x = e.InnerException ?? e; return "{\"error\":" + J.S(p.Name + ": " + x.GetType().Name + ": " + x.Message) + "}"; }
                if (r != null) return r;
            }
            return null;
        }

        // GET /hot/list[?gc=1]; POST /hot/reload?name= | ?all=1, /hot/enable?name=, /hot/disable?name=, /hot/rescan
        // (registered in the route table). The manager window (the LunarLink.Window module) calls the same actions.
        internal static void Routes()
        {
            Api.Get("/hot/list", "?gc=1 : the hot plugins: state, build, newer build on disk, errors, leftovers, old copies still in memory", r => List(r.Query["gc"] == "1"));
            Api.Post("/hot/reload", "?name= : reload a hot plugin from the build on disk now (and turn it on); a wrapped plugin that loads its own content waits for the main menu while a world is loaded; ?all=1 : every one with a newer build", r =>
                r.Query["all"] == "1" ? Timed(() => "\"reloaded\":[" + string.Join(",", ReloadChanged().Select(p => Entry(p, false))) + "]") : One(r, p => Enable(p, reload: true)));
            Api.Post("/hot/enable", "?name= : load and start a hot plugin, and remember it's on", r => One(r, p => Enable(p, reload: false)));
            Api.Post("/hot/disable", "?name= : stop and unload a hot plugin, and remember it's off", r => One(r, Disable));
            Api.Post("/hot/rescan", "Pick up DLLs added to or removed from BepInEx/hot/", r =>
            {
                var (added, removed) = RescanAll();
                return "{\"ok\":true,\"added\":[" + string.Join(",", added.Select(J.S)) + "],\"removed\":[" + string.Join(",", removed.Select(J.S)) + "]}";
            });
        }

        static string Timed(Func<string> body)
        {
            var ms = System.Diagnostics.Stopwatch.StartNew(); var b = body();
            return "{\"ok\":true,\"ms\":" + ms.ElapsedMilliseconds + "," + b + "}";
        }

        static string One(Req r, Func<string, Plugin> act)
        {
            var name = r.Query["name"]; if (string.IsNullOrEmpty(name)) return "{\"error\":\"name= is required\"}";
            var ms = System.Diagnostics.Stopwatch.StartNew();
            var pl = act(name);
            if (pl == null) return "{\"error\":" + J.S("no " + FileOf(name)) + "}";
            return "{\"ok\":" + (pl.Error == null ? "true" : "false") + ",\"ms\":" + ms.ElapsedMilliseconds + ",\"plugin\":" + Entry(pl, false) + "}";
        }

        internal static Plugin Find(string name)
        {
            if (!plugins.TryGetValue(name, out var p) && File.Exists(FileOf(name))) { Rescan(); plugins.TryGetValue(name, out p); }
            return p;
        }

        // Turn it on (and remember that); load and start it if it isn't, or reload it from disk when reload is set.
        internal static Plugin Enable(string name, bool reload)
        {
            var p = Find(name); if (p == null) return null;
            if (!p.Enabled) { p.Enabled = true; Save(); }
            if (reload && Pinned(p)) return p;
            // no forcing it: a copy swapped in mid-world breaks its saves
            if (reload && Waits(p)) { if (!p.ReloadAtMenu) { p.ReloadAtMenu = true; Log("reload of " + p.Name + " waits for the main menu"); } return p; }
            p.ReloadAtMenu = false;
            if (reload || p.Obj == null) Reload(p);
            Bridges.Purge();
            return p;
        }

        // Stop and unload it, and remember it as off.
        internal static Plugin Disable(string name)
        {
            var p = Find(name); if (p == null) return null;
            p.Enabled = false; Save();
            if (Pinned(p)) return p;                                     // off from the next start
            if (p.Obj != null) StopAndUnload(p);
            p.Error = null;
            Bridges.Purge();
            return p;
        }

        // A running wrapped plugin with native hooks can't be stopped safely: its hooks would jump into freed code.
        // It keeps running; a turn-off or a new build takes effect at the next start.
        internal static bool Pinned(Plugin p) => p.Running && p.WrapNative;

        // Most wrapped plugins reload fine mid-game. One that loads its own content (asset bundles: Mars Sweepers' planet)
        // sets things up when the world loads, which a copy swapped in mid-game would miss. In a world, its reload waits
        // until the world is gone - the main menu - and runs then (or at the next start). There's no forcing it: a copy
        // forced in mid-world (tried with Mars Sweepers) ran, but the game refused to save, its world data being the
        // old copy's.
        internal static string WaitReason(Plugin p) => p.Wrapped && p.LoadsContent ? "it loads its own content when the world loads" : null;
        static bool Waits(Plugin p) => p.Running && WaitReason(p) != null && Pump.World() != null;

        // F9's Cancel on a waiting reload
        internal static Plugin CancelReload(string name) { var p = Find(name); if (p != null && p.ReloadAtMenu) { p.ReloadAtMenu = false; Log("reload of " + p.Name + " cancelled"); } return p; }

        // ReloadWatch: a just-reloaded plugin kept throwing. Stopped (it stays on, so the next start loads it cleanly),
        // unless it can't be stopped safely (native hooks): then it's only marked.
        internal static void StopFailed(string name, int errors)
        {
            if (!plugins.TryGetValue(name, out var p)) return;
            bool stop = p.Obj != null && !Pinned(p);
            if (stop) StopAndUnload(p);
            p.Error = "its new copy failed " + errors + " times in its first seconds" + (stop ? ", so it was stopped" : "") + ": restart the game to load it cleanly";
            LunarLink.Plugin.L.LogWarning("hot: " + p.Name + ": " + p.Error);
            Pump.Journal("hot: " + p.Name + ": " + p.Error);
            Bridges.Purge();
        }

        // Every frame: at the main menu (no world, no loading screen), the reloads that were waiting
        static void ReloadWaiting()
        {
            if (!plugins.Values.Any(p => p.ReloadAtMenu) || Pump.World() != null || Pump.Loading()) return;
            foreach (var p in plugins.Values.Where(p => p.ReloadAtMenu).ToList())
            {
                p.ReloadAtMenu = false;
                if (p.Enabled && !Pinned(p)) { Log("main menu: reloading " + p.Name + " now"); Reload(p); }
            }
            Bridges.Purge();
        }

        internal static List<Plugin> ReloadChanged()
        {
            var done = plugins.Values.Where(p => p.Enabled && p.Obj != null && NewerOnDisk(p) && !Pinned(p)).ToList();
            foreach (var p in done.Where(Waits).ToList()) { done.Remove(p); if (!p.ReloadAtMenu) { p.ReloadAtMenu = true; Log("reload of " + p.Name + " waits for the main menu"); } }
            foreach (var p in done) Reload(p);
            Bridges.Purge();
            return done;
        }

        internal static (List<string> added, List<string> removed) RescanAll()
        {
            var r = Rescan();
            Bridges.Purge();
            return r;
        }

        // What the manager window shows, one row per plugin.
        internal readonly struct Row
        {
            public readonly string Name, State, Built, Error; public readonly bool Newer, NeedsRestart, Wrapped;
            public readonly string[] Warnings;
            public Row(string name, string state, string built, string error, bool newer, bool needsRestart, bool wrapped, string[] warnings)
            { Name = name; State = state; Built = built; Error = error; Newer = newer; NeedsRestart = needsRestart; Wrapped = wrapped; Warnings = warnings; }
        }

        internal static List<Row> Rows() => plugins.Values.OrderBy(p => p.Name).Select(p => new Row(p.Name, StateOf(p),
            p.Obj != null ? p.FileTime.ToLocalTime().ToString("HH:mm:ss") : null, p.Error, p.Obj != null && NewerOnDisk(p), p.HasBoot && !p.Booted,
            p.Wrapped, p.Warnings.ToArray())).ToList();

        static string StateOf(Plugin p) => p.Error != null ? "failed" : Pinned(p) && !p.Enabled ? "off at restart" : p.ReloadAtMenu ? "reloads at main menu"
            : !p.Enabled ? "disabled" : p.Running ? "running" : p.Obj != null ? "loaded" : "enabled";

        // Stop the running copy (keeping what its Stop() hands over), load the DLL on disk now, start it with that state.
        static void Reload(Plugin p)
        {
            ReloadWatch.Begin(p);
            string state = p.Running ? StopAndUnload(p) : (p.Obj != null ? Unload(p, null) : null);
            try { Load(p); } catch (Exception e) { Fail(p, "load", e); return; }
            if (started || ((p.Wrapped || p.Early) && chainDone)) Start(p, state);
        }

        static bool NewerOnDisk(Plugin p) => File.Exists(FileOf(p.Name)) && File.GetLastWriteTimeUtc(FileOf(p.Name)) > p.FileTime;

        static Plugin Get(string name)
        {
            if (!plugins.TryGetValue(name, out var p)) plugins[name] = p = new Plugin { Name = name };
            return p;
        }

        static void Load(Plugin p)
        {
            var file = FileOf(p.Name);
            if (p.Ctx != null) Unload(p, null);              // a copy that failed to load half-way is dropped first
            p.Error = null; p.Leftovers.Clear(); p.Warnings.Clear(); p.WrapNative = false; p.LoadsContent = false;
            var folder = Path.GetDirectoryName(file);
            p.Ctx = new Context("hot:" + p.Name + "#" + (p.Loads + 1), true, string.Equals(Path.GetFullPath(folder), Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase) ? null : folder);
            var bytes = File.ReadAllBytes(file);
            var pdbFile = Path.ChangeExtension(file, ".pdb");
            // a normal BepInEx plugin is rewritten by the wrapper module, when it's on (a rewritten copy has no pdb)
            int track = 0; string note = null; bool rewritten = false;
            if (Wrapper != null)
            {
                track = HotTrack.New();
                var w = Wrapper.Prepare(bytes, p.Loads + 1, track, out note, out var warnings, out var native, out var content);
                if (w != null) { bytes = w; p.Warnings.AddRange(warnings); p.WrapNative = native; p.LoadsContent = content; }
                else
                {
                    HotTrack.Discard(track); track = 0;
                    // a hot plugin with its own Il2Cpp types: renamed in each later copy, as wrapped ones are
                    var renamed = p.Loads > 0 ? Wrapper.RenameTypes(bytes, p.Loads + 1) : null;
                    if (renamed != null) { bytes = renamed; rewritten = true; note = "Il2Cpp types renamed"; }
                }
            }
            Assembly asm;
            using (var dll = new MemoryStream(bytes))
            {
                if (track == 0 && !rewritten && File.Exists(pdbFile)) using (var pdb = new MemoryStream(File.ReadAllBytes(pdbFile))) asm = p.Ctx.LoadFromStream(dll, pdb);
                else asm = p.Ctx.LoadFromStream(dll);
            }
            p.Asm = asm;
            HotTrack.Watch(asm, file);                                    // its own Il2Cpp types' bridges are recorded, to be freed
            p.FileTime = File.GetLastWriteTimeUtc(file);
            // the same plugin also in BepInEx/plugins/ (loaded at start) would run twice: refuse, and say which to turn off
            var dup = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins.Values.FirstOrDefault(b =>
                string.Equals(Path.GetFileNameWithoutExtension(b.Location), asm.GetName().Name, StringComparison.OrdinalIgnoreCase) && File.Exists(b.Location));
            if (dup != null)
            {
                Unload(p, null);
                throw new InvalidOperationException(dup.Metadata.Name + " is also in BepInEx/plugins/ (" + dup.Location + ") and already running: disable it there (BEPINEX PLUGINS tab) and restart, or don't load it hot");
            }
            var t = asm.GetTypes().FirstOrDefault(x => x.IsClass && !x.IsAbstract && x.GetConstructor(Type.EmptyTypes) != null
                && M(x, "Start", typeof(string)) != null && M(x, "Stop") != null && M(x, "Stop").ReturnType == typeof(string));
            if (t == null)
            {
                // the wrapper's object for it, which has Start and Stop (and passes on HandleHttp, HttpHelp, UpdateFeed)
                var adapter = track != 0 ? Wrapper.Adapt(asm, file, track) : null;
                if (adapter == null)
                {
                    if (track != 0) HotTrack.Discard(track);
                    throw new InvalidOperationException("no class with Start(string) and string Stop()" + (Wrapper == null
                        ? " (a normal BepInEx plugin runs here only while the LunarLink.Wrap module is on)" : ", and no BepInEx plugin class"));
                }
                p.Obj = adapter; t = adapter.GetType(); p.Wrapped = p.WasWrapped = true;
            }
            else
            {
                if (track != 0) { HotTrack.Discard(track); p.Warnings.Clear(); p.WrapNative = false; }
                p.Obj = Activator.CreateInstance(t);
            }
            p.StartM = M(t, "Start", typeof(string)); p.StopM = M(t, "Stop");
            p.UpdateM = M(t, "Update"); p.LateM = M(t, "LateUpdate");
            p.HttpM = M(t, "HandleHttp", typeof(string), typeof(string), typeof(NameValueCollection), typeof(string));
            if (p.HttpM != null && p.HttpM.ReturnType != typeof(string)) p.HttpM = null;
            p.HelpM = M(t, "HttpHelp"); if (p.HelpM != null && p.HelpM.ReturnType != typeof(string[])) p.HelpM = null;
            p.HasBoot = t.GetMethod("Boot", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null) != null;
            p.WorldM = M(t, "WorldLoaded"); p.WorldGoneM = M(t, "WorldUnloaded");
            p.Early = !p.Wrapped && StartsEarly(t);
            p.Loads++;
            Pump.Journal("hot: loaded " + p.Name + " (copy " + p.Loads + (p.Wrapped ? ", wrapped" + (note != null ? ", " + note : "") : "") + ")");
        }

        // public static bool StartEarly => true (a property or a field): start at game start, before any world
        static bool StartsEarly(Type t)
        {
            try
            {
                const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
                if (t.GetProperty("StartEarly", S) is PropertyInfo pi && pi.PropertyType == typeof(bool)) return (bool)pi.GetValue(null);
                if (t.GetField("StartEarly", S) is FieldInfo fi && fi.FieldType == typeof(bool)) return (bool)fi.GetValue(null);
            }
            catch { }
            return false;
        }

        // World events: WorldLoaded() each time a world has finished loading, and right after Start() when one already
        // has (so a plugin keeps its world set-up in one place); WorldUnloaded() once it's gone (the main menu).
        internal static bool InWorld => Pump.World() != null && !Pump.Loading();
        internal static void WorldChanged(bool loaded)
        {
            foreach (var p in plugins.Values.Where(x => x.Running).ToList()) World(p, loaded);
        }
        static void World(Plugin p, bool loaded)
        {
            var m = loaded ? p.WorldM : p.WorldGoneM; if (m == null) return;
            try { m.Invoke(p.Obj, null); }
            catch (Exception e) { var x = e.InnerException ?? e; LunarLink.Plugin.L.LogError("hot: " + p.Name + " " + m.Name + ": " + x); }
        }

        static MethodInfo M(Type t, string name, params Type[] args) => t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, args, null);

        static void Call(Plugin p, string staticName) => p.Obj.GetType().GetMethod(staticName, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null).Invoke(null, null);

        static void Start(Plugin p, string state)
        {
            try
            {
                p.StartM.Invoke(p.Obj, new object[] { state });
                p.Running = true; Log("started " + p.Name + " (copy " + p.Loads + (p.Wrapped ? ", wrapped" : p.Early ? ", early" : "") + ")");
                if (InWorld) World(p, true);
            }
            catch (Exception e) { Fail(p, "Start", e); Unload(p, null); }   // whatever it did before throwing (patches, routes) goes
        }

        static void Frame(Plugin p, MethodInfo m)
        {
            try { m.Invoke(p.Obj, null); }
            catch (Exception e) { Fail(p, m.Name, e); }            // a throwing frame method turns the plugin off, logged once
        }

        static void Fail(Plugin p, string where, Exception e)
        {
            var x = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            p.Error = where + ": " + x;
            LunarLink.Plugin.L.LogError("hot: " + p.Name + " " + p.Error);
            if (p.Running) StopAndUnload(p);
        }

        // Stop the running copy, then drop it. Returns the state its Stop() handed over.
        static string StopAndUnload(Plugin p)
        {
            string state = null;
            if (p.Running)
            {
                p.Running = false;
                try { state = (string)p.StopM.Invoke(p.Obj, null); }
                catch (Exception e) { var x = e.InnerException ?? e; LunarLink.Plugin.L.LogError("hot: " + p.Name + " Stop: " + x); }
                if (p.Obj is IWrapped w) p.Leftovers.AddRange(w.Leftovers);
                else if (p.Asm != null)
                {
                    // whatever of its own types is still there is destroyed (journalled); one it destroyed itself in Stop() is
                    // only removed by Unity at the end of the frame, so it's not counted against the plugin
                    var (_, problem) = HotTrack.DestroyObjects(p.Asm);
                    if (problem != null) p.Leftovers.Add(problem);
                    HotTrack.Forget(p.Asm);
                }
            }
            return Unload(p, state);
        }

        static string Unload(Plugin p, string state)
        {
            ForceUnpatch(p);
            var asm = p.Asm;
            if (asm != null) { int n = Api.RemoveFrom(asm); if (n > 0) Pump.Journal("hot: " + p.Name + "'s " + n + " routes removed"); }
            p.State = state;
            var ctx = p.Ctx;
            p.Obj = null; p.Ctx = null; p.Asm = null; p.Wrapped = false; p.Early = false; p.StartM = p.StopM = p.UpdateM = p.LateM = p.HttpM = p.HelpM = p.WorldM = p.WorldGoneM = null;
            if (ctx != null)
            {
                p.Old.Add(new WeakReference(ctx));
                if (ctx.IsCollectible) try { ctx.Unload(); } catch (Exception e) { p.Leftovers.Add("unload: " + e.Message); }
            }
            Pump.Journal("hot: unloaded " + p.Name);
            return state;
        }

        // Any Harmony patch whose owner is the plugin's name (or starts with "name.") or whose patch method lives in the
        // plugin's copy is still in after Stop(): take it out and report it.
        static void ForceUnpatch(Plugin p)
        {
            var asm = p.Asm;
            var owners = new HashSet<string>();
            foreach (var m in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(m);
                if (info == null) continue;
                foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                    if (patch.owner.Equals(p.Name, StringComparison.OrdinalIgnoreCase) || patch.owner.StartsWith(p.Name + ".", StringComparison.OrdinalIgnoreCase)
                        || (asm != null && patch.PatchMethod?.DeclaringType?.Assembly == asm))
                        owners.Add(patch.owner);
            }
            foreach (var o in owners)
            {
                p.Leftovers.Add("Harmony patches still in under '" + o + "' after Stop(): removed");
                Harmony.UnpatchID(o);
            }
            if (owners.Count > 0) LunarLink.Plugin.L.LogWarning("hot: " + p.Name + " left Harmony patches in after Stop(): " + string.Join(", ", owners));
        }

        // for the support file, read off the main thread: a copy of the list first
        internal static string ListJson() => List(false);
        internal static List<(string name, string error, int leftovers, bool newer)> Snapshot() =>
            plugins.Values.ToList().Select(p => (p.Name, p.Error, p.Leftovers.Count, p.Obj != null && NewerOnDisk(p))).ToList();

        static string List(bool gc)
        {
            if (gc) { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
            var sb = new StringBuilder("{\"dir\":" + J.S(Dir) + ",\"bridges\":" + Bridges.Status() + ",\"plugins\":[");
            bool first = true;
            foreach (var p in plugins.Values.ToList()) { if (!first) sb.Append(','); first = false; sb.Append(Entry(p, true)); }
            return sb.Append("]}").ToString();
        }

        static string Entry(Plugin p, bool full)
        {
            var s = "{\"name\":" + J.S(p.Name) + ",\"state\":" + J.S(StateOf(p)) + ",\"built\":" + J.S(p.Obj != null ? p.FileTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : null)
                + ",\"error\":" + J.S(p.Error) + ",\"loads\":" + p.Loads + ",\"boot\":" + (p.HasBoot ? (p.Booted ? "\"done\"" : "\"restart to boot\"") : "false")
                + ",\"newerOnDisk\":" + (p.Obj != null && NewerOnDisk(p) ? "true" : "false")
                + ",\"leftovers\":[" + string.Join(",", p.Leftovers.Select(J.S)) + "]"
                + ",\"reloadAtMenu\":" + (p.ReloadAtMenu ? "true" : "false")
                + ",\"wrapped\":" + (p.Wrapped || (p.Obj == null && p.WasWrapped) ? "true" : "false") + ",\"warnings\":[" + string.Join(",", p.Warnings.Select(J.S)) + "]";
            if (full) s += ",\"oldCopiesAlive\":" + p.Old.Count(w => w.IsAlive) + ",\"oldCopies\":" + p.Old.Count;
            return s + "}";
        }

        static void Log(string s) { LunarLink.Plugin.L.LogInfo("hot: " + s); Pump.Journal("hot: " + s); }
    }
}
