// Hot plugins: DLLs in BepInEx/hot/ loaded by LunarLink, each into its own AssemblyLoadContext, so a new build can
// replace the running one without a game restart. A plugin is found by method names (no reference to LunarLink):
//   public class with a parameterless constructor and   void Start(string state)   string Stop()
//   optional: void Update()  void LateUpdate()  string HandleHttp(string method, string path, NameValueCollection query, string body)
//             static void Boot()   (once, at game start, before MoonFramework's build menu: buildings and cargo go here)
// Which plugins are on is kept in BepInEx/hot/hot.json ({"enabled":[names], "disabled":[names]}): a DLL in neither list
// (new to the folder) stays off until it's enabled - except LunarLink's own modules (LunarLink.*), which are on unless
// they're in "disabled", so an update that brings a new module runs it and a module turned off on purpose stays off.
// optional: string[] HttpHelp()  (one line per endpoint, "GET /path?x= | what it does", for /help and the API tab)
//           string[] SettingsButtons()  (one line per button: "id | label | description", for F9's Settings page)
//           string ClickSettingButton(string id)  (run one declared button action; F9 reloads the plugin afterwards)
// A DLL without such a class but with a normal BepInEx plugin class runs wrapped, when the LunarLink.Wrap module is on:
// the module rewrites it as it loads and hands back an object with Start and Stop (see IWrapper below and Track.cs).
// Everything here runs on the main thread (Load, Pump's frames, and requests, which Pump answers).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;

namespace LunarLink
{
    static partial class Hot
    {
        internal sealed class Context : AssemblyLoadContext
        {
            readonly string folder;                                          // the plugin's own folder (hot/<name>/), or null
            // collectible: an unloaded copy is freed (see Bridges.cs)
            public Context(string name, bool collectible, string folder) : base(name, collectible) { this.folder = folder; }
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
            public MethodInfo StartM, StopM, UpdateM, LateM, HttpM, HelpM, SettingsButtonsM, ClickSettingButtonM;
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
        // The wrapper slot, filled by the LunarLink.Wrap module while it runs.
        internal static IWrapper Wrapper;

        static bool chainDone;                                            // BepInEx has loaded its plugins: wrapped ones can run

        static void Log(string s) { LunarLink.Plugin.L.LogInfo("hot: " + s); Pump.Journal("hot: " + s); }
    }
}
