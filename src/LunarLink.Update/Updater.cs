// The LunarLink.Update module: updating LunarLink from GitHub, when asked (F9's UPDATES tab, or the endpoints), or once at
// each game start if "check at game start" is on (off by default).
// It's a module so it updates like one; the core keeps only Updates (Finish at game start, and the slot the F9 window
// reaches this module through). Two sources:
//   releases            a whole LunarLink version (the latest GitHub release's zip)
//   the module channel  single modules as built from main (the "modules" prerelease: one DLL each, and modules.json
//                        with each one's source fingerprint, SHA-256 and the core fingerprint it needs;
//                        tools/publish-modules.sh publishes it from CI)
//   POST /update/check                ask GitHub for both (nothing is sent but the requests themselves)
//   POST /update/modify?auto=1|0      check by itself once at every game start (off by default: checking is manual)
//   GET  /update                      what's known: the running version, the latest release, each module's state
//   POST /update/apply                install the latest release:
//                                     - same core (the release's manifest.json has the installed coreHash): its modules go
//                                       into BepInEx/hot/ and reload in place, no restart;
//                                     - a new core: the running LunarLink.dll is renamed to LunarLink.dll.old (a loaded DLL
//                                       can be renamed, not overwritten) and the new one written in its place; the modules
//                                       wait in plugins/LunarLink/update/ so a new module never runs on the old core. The
//                                       next start loads the new core, which moves them into BepInEx/hot/ (Updates.Finish);
//                                       this module then checks again for newer module builds.
//   POST /update/apply?module=<name>  install one module from the channel (or module=all: every one with a newer build),
//                                     if it was built for the installed core; it reloads in place
//   POST /update/apply?plugin=<name>  a plugin's own update, from where its UpdateFeed() says (PluginUpdates.cs)
// Every download is checked against a SHA-256 (GitHub's for a release zip, modules.json's for a module); only
// LunarLink's own files are taken from a zip (BepInEx/plugins/LunarLink/*, BepInEx/hot/LunarLink.*.dll).
// manifest.json (plugins/LunarLink/) records what's installed: the version, the core's fingerprint and each module's.
// Downloads run on a background thread; the reloads run on the main thread (Tick, from this module's Update). When
// this module is among the modules updated, it reloads itself last and the new copy checks again.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace LunarLink
{
    // Updating LunarLink from GitHub's releases and the module channel, when asked (or at game start if chosen): check, see each module's
    // state, install a release (a new core needs a restart) or single modules (reloaded in place).
    public class UpdateModule
    {
        public void Start(string state)
        {
            Updater.Routes();
            Updates.Status = Updater.Json; Updates.Check = Updater.Check; Updates.Apply = Updater.ApplyAny;
            // after a core update finished at this start, or after this module updated itself: look again; and at a game
            // start (state null: not a reload) when "check at game start" is on
            if (Updates.JustFinished || state == "self-updated" || (state == null && Updater.Auto)) { Updates.JustFinished = false; Updater.CheckSoon = true; }
        }

        public string Stop()
        {
            Updates.Status = null; Updates.Check = null; Updates.Apply = null;
            return Updater.SelfUpdating ? "self-updated" : "reloaded";       // non-null: the next copy is a reload, not a game start
        }

        public void Update() => Updater.Tick();
    }

    static partial class Updater
    {
        const string Repo = "swheettaos/lunarlink";
        const string Channel = "https://github.com/" + Repo + "/releases/download/modules/modules.json";
        const string Plugins = "BepInEx/plugins/LunarLink/", HotFolder = "BepInEx/hot/";
        static string ManifestPath => Path.Combine(Api.DataDir, "manifest.json");

        // LunarLink's installed version: manifest.json's (an update that only changed the modules rewrites it), or the
        // core's own [BepInPlugin] version when there's no manifest (an older install)
        internal static string Running => Manifest().version ?? BepInEx.MetadataHelper.GetMetadata(typeof(Plugin)).Version.ToString();

        // A new core installed while the game runs only loads at the next start: until then the core in memory is the old
        // one. Its build stamp (Pump.PluginVersion, from the loaded DLL) then differs from installed-version, which the
        // update wrote with the new core. Every check says "restart to finish" while that's so, not "up to date".
        internal static bool RestartPending
        {
            get
            {
                try
                {
                    var f = Path.Combine(Api.DataDir, "installed-version");
                    return File.Exists(f) && File.ReadAllText(f).Trim() is string inst && inst.Length > 0 && inst != Pump.PluginVersion;
                }
                catch { return false; }
            }
        }
        // the core in memory now: its [BepInPlugin] version
        static string Loaded => BepInEx.MetadataHelper.GetMetadata(typeof(Plugin)).Version.ToString();

        // what's installed, from manifest.json: the version, the core's fingerprint, each module's
        static (string version, string core, Dictionary<string, string> modules) Manifest()
        {
            var mods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(ManifestPath)) return (null, null, mods);
                using var d = JsonDocument.Parse(File.ReadAllText(ManifestPath)); var r = d.RootElement;
                if (r.TryGetProperty("modules", out var m) && m.ValueKind == JsonValueKind.Object)
                    foreach (var p in m.EnumerateObject()) mods[p.Name] = p.Value.GetString();
                return (r.TryGetProperty("version", out var v) ? v.GetString() : null, r.TryGetProperty("coreHash", out var h) ? h.GetString() : null, mods);
            }
            catch { return (null, null, mods); }
        }

        // a module in the channel, and where its update stands
        internal sealed class Module
        {
            public string Name, Version, SourceHash, Sha256, Url, Note, Built, Commit, Error;
            public long Size;
            public string State;     // up to date, available, needs core (built for another core), downloading, reloading, updated, failed
        }

        static readonly object gate = new object();
        // the release: idle, checking, up to date, available, downloading, reloading, done, restart, failed
        static string state = "idle", latest, notes, page, zipUrl, digest, error, detail;
        static long size; static bool? coreChanged;
        static readonly List<Module> modules = new List<Module>();
        static string channelError, channelModified;
        static List<string> toReload;                    // a release's modules to reload on the main thread (Tick)
        static List<Module> modulesToReload;             // the channel's modules to reload (Tick)
        internal static bool CheckSoon;                  // check at the next frame (after a core update, a self-update, or at game start)

        // "Check for updates at game start": off by default (checking is manual); kept in update_auto.txt
        static string AutoPath => Path.Combine(Api.DataDir, "update_auto.txt");
        internal static bool Auto { get { try { return File.ReadAllText(AutoPath).Trim() == "1"; } catch { return false; } } }
        internal static string SetAuto(bool on)
        {
            File.WriteAllText(AutoPath, on ? "1" : "0");
            Plugin.L.LogInfo("update: check at game start " + (on ? "on" : "off"));
            return new JsonOut().Obj().Bool("ok", true).Bool("autoCheck", on);
        }
        internal static bool SelfUpdating;               // this module is reloading itself: its Stop hands that over

        internal static void Routes()
        {
            Api.Get("/update", "What LunarLink's updater knows: the running version, the latest release, each module in the " +
                "module channel, each plugin with an UpdateFeed(), and how far an update got", r => Json());
            Api.Post("/update/modify",
                "?auto=1|0 : check for updates by itself once a moon is loaded, at every game start (off by default: checking " +
                    "is manual)", r => SetAuto(r.Query["auto"] == "1"));
            Api.Ask("/update/check",
                "Ask GitHub for LunarLink's latest release and the module channel, and each plugin's update feed, then read GET /update", r => Check());
            Api.Post("/update/apply",
                "Install the latest release (modules reload in place; a new core needs a restart), or ?module=<name>|all: " +
                    "modules from the channel, reloaded in place, or ?plugin=<name>: a plugin's own update (UpdateFeed). " +
                    "?force=1 reinstalls the same release", r =>
                RestartPending ? Api.Result(false, RestartFirst)
                : !string.IsNullOrEmpty(r.Query["plugin"]) ? PluginUpdates.Apply(r.Query["plugin"])
                : string.IsNullOrEmpty(r.Query["module"]) ? Apply(r.Query["force"] == "1") : ApplyModules(r.Query["module"]));
        }

        internal static string Json()
        {
            // a new core waiting for the restart, whatever the last check said (or none yet)
            bool pending = RestartPending;
            var o = new JsonOut();
            lock (gate)
            {
                o.Obj().Bool("ok", true).Str("running", Running).Str("loaded", Loaded).Bool("restartPending", pending).Bool("autoCheck", Auto)
                 .Str("state", pending && (state == "idle" || state == "up to date" || state == "available") ? "restart" : state)
                 .Str("latest", latest).Str("page", page).Num("size", size);
                if (coreChanged == null) o.Null("coreChanged"); else o.Bool("coreChanged", coreChanged.Value);
                o.Str("detail", detail).Str("error", error).Str("notes", notes)
                 .Obj("channel").Str("error", channelError).Str("published", channelModified).Arr("modules");
                foreach (var m in modules)
                    o.Obj().Str("name", m.Name).Str("version", m.Version).Str("installed", Installed(m.Name)).Str("state", m.State).Str("note", m.Note)
                     .Str("built", m.Built).Str("commit", m.Commit).Num("size", m.Size).Str("error", m.Error).End();
                o.End().End();
            }
            PluginUpdates.Write(o.Arr("plugins"));
            return o;
        }

        // a module's version as installed in BepInEx/hot/: its assembly version ("dev" for a local build), read from the
        // file and kept until the file changes
        static readonly Dictionary<string, (DateTime at, string v)> installedSeen = new Dictionary<string, (DateTime, string)>();
        internal static string Installed(string name)
        {
            try
            {
                var f = Hot.FileOf(name); if (!File.Exists(f)) return null;
                var at = File.GetLastWriteTimeUtc(f);
                lock (installedSeen)
                {
                    if (installedSeen.TryGetValue(name, out var seen) && seen.at == at) return seen.v;
                    var v = System.Reflection.AssemblyName.GetAssemblyName(f).Version;
                    // 1.0.0.0: built before modules had versions
                    var s = v == null || v == new Version(1, 0, 0, 0) ? null : v == new Version(0, 0, 0, 0) ? "dev"
                        : v.Revision > 0 ? v.ToString(4) : v.ToString(3);
                    installedSeen[name] = (at, s); return s;
                }
            }
            catch { return null; }
        }

        // the same source is offered again only to give a module built before versions its number (its DLL has none);
        // the same code under another number (a release's x.y.z, the channel's x.y.z.1) is up to date ("dev" never counts)
        static bool Newer(string channel, string installed) => Version.TryParse(channel, out _) && installed == null;

        internal static bool Busy
        {
            get { lock (gate) if (state == "checking" || state == "downloading" || state == "reloading"
                || modules.Any(m => m.State == "downloading" || m.State == "reloading")) return true; return PluginUpdates.Busy; }
        }

        static HttpClient Client()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LunarLink-updater");
            c.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            return c;
        }

        // A download address made fresh: the module channel's files are replaced in place, and a cache on the way (seen
        // in the game, 2026-11-13: an old modules.json minutes after the new one was published) must not answer for them.
        internal static string Fresh(string url) => url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;

        static void Set(string s, string d = null,
            string e = null) { lock (gate) { state = s; detail = d; error = e;
            } if (e != null) Plugin.L.LogWarning("update: " + e); else if (d != null) Plugin.L.LogInfo("update: " + d); }

        // Every frame, on the main thread: reload what an update put in place, and the check after a core update.
        internal static void Tick()
        {
            PluginUpdates.Tick();
            if (CheckSoon) { CheckSoon = false; Plugin.L.LogInfo("update: checking again for newer module builds"); Check(); }
            List<string> names; List<Module> mods;
            lock (gate) { names = toReload; toReload = null; mods = modulesToReload; modulesToReload = null; }
            if (names != null)
            {
                var done = new List<string>(); var failed = new List<string>();
                foreach (var n in names) { var ok = Reload(n); if (ok == true) done.Add(n); else if (ok == false) failed.Add(n); }
                if (failed.Count > 0) Set("failed", "reloaded " + string.Join(", ", done), "these modules failed to start: " + string.Join(", ", failed));
                else Set("done", "LunarLink " + latest + ": "
                    + (done.Count > 0 ? "reloaded " + string.Join(", ", done) : "modules updated") + ", no restart needed");
            }
            if (mods != null)
            {
                const string Self = "LunarLink.Update";
                Module self = null;
                foreach (var m in mods)
                {
                    try { Record(m.Name, m.SourceHash); } catch (Exception e) { Plugin.L.LogWarning("update: manifest: " + e.Message); }
                    if (m.Name == Self) { self = m; continue; }          // this module last: reloading it ends this copy
                    var ok = Reload(m.Name);
                    lock (gate) { m.State = ok == false ? "failed" : "updated"; if (ok == false) m.Error = "it didn't start: see F9's HOT PLUGINS tab"; }
                    Plugin.L.LogInfo("update: " + m.Name + (ok == false ? " failed to start" : " updated to " + m.Commit));
                }
                if (self != null)
                {
                    Plugin.L.LogInfo("update: " + Self + " updated to " + self.Commit + "; reloading itself");
                    SelfUpdating = true; Reload(Self); SelfUpdating = false;
                }
            }
        }

        // reload one module from its new file: true started, false failed, null turned off (it loads the new build when turned on)
        static bool? Reload(string name)
        {
            var p = Hot.Find(name);
            if (p == null) { Hot.RescanAll(); p = Hot.Find(name); }
            if (p == null || !p.Enabled) return null;
            Hot.Enable(name, reload: true);
            return p.Error == null;
        }
    }
}
