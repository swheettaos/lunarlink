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
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    static class Updater
    {
        const string Repo = "swheettaos/lunarlink";
        const string Channel = "https://github.com/" + Repo + "/releases/download/modules/modules.json";
        const string Plugins = "BepInEx/plugins/LunarLink/", HotFolder = "BepInEx/hot/";
        static string ManifestPath => Path.Combine(Api.DataDir, "manifest.json");

        // LunarLink's installed version: manifest.json's (an update that only changed the modules rewrites it), or the
        // core's own [BepInPlugin] version when there's no manifest (an older install)
        internal static string Running => Manifest().version ?? BepInEx.MetadataHelper.GetMetadata(typeof(Plugin)).Version.ToString();

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
            public string Name, SourceHash, Sha256, Url, Note, Built, Commit, Error;
            public long Size;
            public string State;     // up to date, available, needs core (built for another core), downloading, reloading, updated, failed
        }

        static readonly object gate = new object();
        // the release: idle, checking, up to date, available, downloading, reloading, done, restart, failed
        static string state = "idle", latest, notes, page, zipUrl, digest, error, detail;
        static long size; static bool? coreChanged;
        static readonly List<Module> modules = new List<Module>();
        static string channelCore, channelError, channelModified;
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
            return "{\"ok\":true,\"autoCheck\":" + (on ? "true" : "false") + "}";
        }
        internal static bool SelfUpdating;               // this module is reloading itself: its Stop hands that over

        internal static void Routes()
        {
            Api.Get("/update", "What LunarLink's updater knows: the running version, the latest release, each module in the module channel, each plugin with an UpdateFeed(), and how far an update got", r => Json());
            Api.Post("/update/modify", "?auto=1|0 : check for updates by itself once a moon is loaded, at every game start (off by default: checking is manual)", r => SetAuto(r.Query["auto"] == "1"));
            Api.Ask("/update/check", "Ask GitHub for LunarLink's latest release and the module channel, and each plugin's update feed, then read GET /update", r => Check());
            Api.Post("/update/apply", "Install the latest release (modules reload in place; a new core needs a restart), or ?module=<name>|all: modules from the channel, reloaded in place, or ?plugin=<name>: a plugin's own update (UpdateFeed). ?force=1 reinstalls the same release", r =>
                !string.IsNullOrEmpty(r.Query["plugin"]) ? PluginUpdates.Apply(r.Query["plugin"])
                : string.IsNullOrEmpty(r.Query["module"]) ? Apply(r.Query["force"] == "1") : ApplyModules(r.Query["module"]));
        }

        internal static string Json()
        {
            lock (gate)
                return "{\"ok\":true,\"running\":" + J.S(Running) + ",\"autoCheck\":" + (Auto ? "true" : "false") + ",\"state\":" + J.S(state) + ",\"latest\":" + J.S(latest) + ",\"page\":" + J.S(page)
                    + ",\"size\":" + size + ",\"coreChanged\":" + (coreChanged == null ? "null" : coreChanged.Value ? "true" : "false")
                    + ",\"detail\":" + J.S(detail) + ",\"error\":" + J.S(error) + ",\"notes\":" + J.S(notes)
                    + ",\"channel\":{\"error\":" + J.S(channelError) + ",\"published\":" + J.S(channelModified) + ",\"modules\":[" + string.Join(",", modules.Select(m =>
                        "{\"name\":" + J.S(m.Name) + ",\"state\":" + J.S(m.State) + ",\"note\":" + J.S(m.Note) + ",\"built\":" + J.S(m.Built)
                        + ",\"commit\":" + J.S(m.Commit) + ",\"size\":" + m.Size + ",\"error\":" + J.S(m.Error) + "}")) + "]},\"plugins\":" + PluginUpdates.Json() + "}";
        }

        internal static bool Busy
        {
            get { lock (gate) if (state == "checking" || state == "downloading" || state == "reloading" || modules.Any(m => m.State == "downloading" || m.State == "reloading")) return true; return PluginUpdates.Busy; }
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

        static void Set(string s, string d = null, string e = null) { lock (gate) { state = s; detail = d; error = e; } if (e != null) Plugin.L.LogWarning("update: " + e); else if (d != null) Plugin.L.LogInfo("update: " + d); }

        // the slot's Apply (the F9 window's buttons): "" the latest release, a module's name or "all", "plugin:<name>" a
        // plugin's own update, "auto:1" / "auto:0" the "check at game start" setting
        internal static string ApplyAny(string what) => string.IsNullOrEmpty(what) ? Apply(false)
            : what.StartsWith("plugin:") ? PluginUpdates.Apply(what.Substring(7))
            : what.StartsWith("auto:") ? SetAuto(what == "auto:1") : ApplyModules(what);

        internal static string Check()
        {
            if (Busy) return Json();
            lock (gate) { state = "checking"; error = null; detail = null; coreChanged = null; channelError = null; }
            PluginUpdates.Discover();                            // on this (main) thread: asks each plugin for UpdateFeed()
            Task.Run(CheckNow);
            return Json();
        }

        static async Task CheckNow()
        {
            using var c = Client();
            await CheckChannel(c);                                   // first, so the release's state is the last word
            await PluginUpdates.CheckAll(c);
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Repo + "/releases/latest");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                var res = await c.SendAsync(req); res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync()); var r = doc.RootElement;
                string tag = r.GetProperty("tag_name").GetString() ?? "", ver = tag.TrimStart('v');
                JsonElement asset = default; bool found = false;
                foreach (var a in r.GetProperty("assets").EnumerateArray())
                {
                    var n = a.GetProperty("name").GetString() ?? "";
                    if (n.StartsWith("LunarLink-") && n.EndsWith(".zip")) { asset = a; found = true; break; }
                }
                lock (gate)
                {
                    latest = ver; notes = r.TryGetProperty("body", out var b) ? b.GetString() : null; page = r.GetProperty("html_url").GetString();
                    zipUrl = found ? asset.GetProperty("browser_download_url").GetString() : null;
                    digest = found && asset.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                    size = found ? asset.GetProperty("size").GetInt64() : 0;
                }
                if (!found) { Set("failed", null, "release " + tag + " has no LunarLink zip"); return; }
                bool newer = Version.TryParse(ver, out var v) && Version.TryParse(Running, out var mine) && v > mine;
                Set(newer ? "available" : "up to date", newer ? "LunarLink " + ver + " is available (running " + Running + ")" : "LunarLink " + Running + " is the latest release");
            }
            catch (Exception e) { Set("failed", null, "check: " + (e.InnerException ?? e).Message); }
        }

        // modules.json from the module channel: each module's state against what's installed
        static async Task CheckChannel(HttpClient c)
        {
            try
            {
                var res = await c.GetAsync(Fresh(Channel));
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound) { lock (gate) { modules.Clear(); channelCore = null; } return; }   // no channel yet
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync()); var r = doc.RootElement;
                var installed = Manifest(); var list = new List<Module>();
                string core = r.GetProperty("coreHash").GetString();
                foreach (var p in r.GetProperty("modules").EnumerateObject())
                {
                    var e = p.Value; string Str(string k) => e.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                    var m = new Module { Name = p.Name, SourceHash = Str("sourceHash"), Sha256 = Str("sha256"), Url = Str("url"), Note = Str("note"),
                                         Built = Str("built"), Commit = Str("commit"), Size = e.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0 };
                    m.State = core != installed.core ? "needs core"
                            : installed.modules.TryGetValue(m.Name, out var have) && have == m.SourceHash ? "up to date" : "available";
                    list.Add(m);
                }
                lock (gate) { modules.Clear(); modules.AddRange(list.OrderBy(m => m.Name)); channelCore = core; channelModified = res.Content.Headers.LastModified?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"; }
            }
            catch (Exception e) { lock (gate) channelError = "module channel: " + (e.InnerException ?? e).Message; Plugin.L.LogWarning("update: module channel: " + (e.InnerException ?? e).Message); }
        }

        internal static string Apply(bool force)
        {
            if (Busy) return Json();
            lock (gate)
            {
                if (state != "available" && !(force && zipUrl != null)) return "{\"ok\":false,\"reason\":" + J.S(zipUrl == null ? "check first: POST /update/check" : "no newer release (force=1 reinstalls it)") + "}";
                state = "downloading"; error = null; detail = "downloading " + latest;
            }
            Task.Run(ApplyNow);
            return Json();
        }

        static async Task ApplyNow()
        {
            string url, want; lock (gate) { url = zipUrl; want = digest; }
            try
            {
                byte[] zip;
                using (var c = Client()) zip = await c.GetByteArrayAsync(url);
                if (string.IsNullOrEmpty(want) || !want.StartsWith("sha256:")) { Set("failed", null, "GitHub published no SHA-256 for the zip: not installed"); return; }
                if ("sha256:" + Sha(zip) != want) { Set("failed", null, "the download doesn't match GitHub's SHA-256: not installed"); return; }
                Install(zip);
            }
            catch (Exception e) { Set("failed", null, "install: " + (e.InnerException ?? e).Message); }
        }

        // ?module=<name> or all: the channel's newer builds of those modules
        internal static string ApplyModules(string which)
        {
            if (Busy) return Json();
            List<Module> pick;
            lock (gate)
            {
                if (modules.Count == 0) return "{\"ok\":false,\"reason\":\"check first: POST /update/check (no module channel known)\"}";
                pick = modules.Where(m => (which == "all" || string.Equals(m.Name, which, StringComparison.OrdinalIgnoreCase)) && m.State == "available").ToList();
                if (pick.Count == 0)
                {
                    var m = modules.FirstOrDefault(x => string.Equals(x.Name, which, StringComparison.OrdinalIgnoreCase));
                    return "{\"ok\":false,\"reason\":" + J.S(which != "all" && m == null ? "no module " + which + " in the channel"
                        : m != null && m.State == "needs core" ? m.Name + " is built for a newer core: install the next LunarLink release first"
                        : "no newer module build") + "}";
                }
                foreach (var m in pick) { m.State = "downloading"; m.Error = null; }
                picked = pick;
            }
            Task.Run(ApplyModulesNow);
            return Json();
        }

        static List<Module> picked;                      // the modules ApplyModulesNow downloads (handed over in a field: a lambda here needs a compiler attribute these references lack)
        static async Task ApplyModulesNow()
        {
            List<Module> pick; lock (gate) { pick = picked; picked = null; }
            var ready = new List<Module>();
            using var c = Client();
            foreach (var m in pick)
            {
                try
                {
                    if (string.IsNullOrEmpty(m.Sha256) || string.IsNullOrEmpty(m.Url)) throw new InvalidOperationException("modules.json names no SHA-256 or address");
                    var dll = await c.GetByteArrayAsync(Fresh(m.Url));
                    if (Sha(dll) != m.Sha256.ToLowerInvariant()) throw new InvalidOperationException("the download doesn't match modules.json's SHA-256: not installed");
                    Put(Path.Combine(Hot.Dir, m.Name + ".dll"), dll);
                    lock (gate) m.State = "reloading";
                    ready.Add(m);
                }
                catch (Exception e) { lock (gate) { m.State = "failed"; m.Error = (e.InnerException ?? e).Message; } Plugin.L.LogWarning("update: " + m.Name + ": " + m.Error); }
            }
            lock (gate) modulesToReload = ready;
        }

        static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        // Write a file without ever writing over one in use: next to it first, then moved into place.
        static void Put(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path + ".new", bytes);
            File.Move(path + ".new", path, true);
        }

        // manifest.json: this module's fingerprint is now the installed one
        static void Record(string module, string sourceHash)
        {
            var node = (File.Exists(ManifestPath) ? JsonNode.Parse(File.ReadAllText(ManifestPath)) : null) as JsonObject ?? new JsonObject();
            if (node["modules"] is not JsonObject mods) { mods = new JsonObject(); node["modules"] = mods; }
            mods[module] = sourceHash;
            Put(ManifestPath, System.Text.Encoding.UTF8.GetBytes(node.ToJsonString() + "\n"));
        }

        static void Install(byte[] zipBytes)
        {
            var core = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);     // file name -> bytes, for plugins/LunarLink/
            var mods = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);     // for BepInEx/hot/
            using (var z = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
                foreach (var e in z.Entries)
                {
                    var n = e.FullName.Replace('\\', '/'); if (n.EndsWith("/")) continue;
                    string name = Path.GetFileName(n); var ms = new MemoryStream(); using (var s = e.Open()) s.CopyTo(ms);
                    if (n == Plugins + name && !name.Contains("..")) core[name] = ms.ToArray();
                    else if (n == HotFolder + name && name.StartsWith("LunarLink.") && name.EndsWith(".dll")) mods[name] = ms.ToArray();
                    // anything else in the zip is ignored
                }
            if (!core.ContainsKey("LunarLink.dll") || mods.Count == 0) { Set("failed", null, "the zip doesn't hold LunarLink's files"); return; }

            string newHash = null;
            if (core.TryGetValue("manifest.json", out var mf))
                try { using var d = JsonDocument.Parse(mf); newHash = d.RootElement.TryGetProperty("coreHash", out var h) ? h.GetString() : null; } catch { }
            string oldHash = Manifest().core;
            bool changed = newHash == null || oldHash == null || newHash != oldHash;
            lock (gate) coreChanged = changed;

            if (!changed)
            {
                // same core: the modules into BepInEx/hot/ and reloaded (Tick); the core's files stay as they are,
                // installed-version too (it names the running core's build)
                foreach (var kv in mods) Put(Path.Combine(Hot.Dir, kv.Key), kv.Value);
                if (mf != null) Put(ManifestPath, mf);
                lock (gate) { toReload = mods.Keys.Select(Path.GetFileNameWithoutExtension).ToList(); state = "reloading"; detail = "reloading " + toReload.Count + " modules"; }
                return;
            }
            // a new core: swapped in for the next start, the modules waiting beside it
            var dir = Api.DataDir; var dll = Path.Combine(dir, "LunarLink.dll");
            File.WriteAllBytes(dll + ".new", core["LunarLink.dll"]);
            if (File.Exists(dll + ".old")) File.Delete(dll + ".old");
            File.Move(dll, dll + ".old");                    // the loaded DLL can be renamed
            File.Move(dll + ".new", dll);
            foreach (var kv in core) if (kv.Key != "LunarLink.dll") Put(Path.Combine(dir, kv.Key), kv.Value);
            var staged = Path.Combine(dir, "update");
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            foreach (var kv in mods) Put(Path.Combine(staged, kv.Key), kv.Value);
            lock (gate) foreach (var m in modules) m.State = "needs core";     // the channel is judged again after the restart
            Set("restart", "LunarLink " + latest + " is installed: restart the game to finish (the core loads only at start); it checks for newer module builds once it's back");
        }

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
                else Set("done", "LunarLink " + latest + ": " + (done.Count > 0 ? "reloaded " + string.Join(", ", done) : "modules updated") + ", no restart needed");
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
