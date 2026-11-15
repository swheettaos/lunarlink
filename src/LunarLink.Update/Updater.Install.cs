using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace LunarLink
{
    // Installing: the release (a new core waits for the next start), single modules (reloaded in place), each download
    // checked against GitHub's SHA-256 and recorded in the manifest.
    static partial class Updater
    {
        // the slot's Apply (the F9 window's buttons): "" the latest release, a module's name or "all", "plugin:<name>" a
        // plugin's own update, "auto:1" / "auto:0" the "check at game start" setting
        internal static string ApplyAny(string what) => RestartPending && !(what ?? "").StartsWith("auto:") ? RestartFirst
            : string.IsNullOrEmpty(what) ? Apply(false)
            : what.StartsWith("plugin:") ? PluginUpdates.Apply(what.Substring(7))
            : what.StartsWith("auto:") ? SetAuto(what == "auto:1") : ApplyModules(what);

        internal static string Apply(bool force)
        {
            if (Busy) return Json();
            lock (gate)
            {
                if (state != "available" && !(force && zipUrl != null)) return Api.Result(false, zipUrl == null
                    ? "check first: POST /update/check" : "no newer release (force=1 reinstalls it)");
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
                if (string.IsNullOrEmpty(want)
                    || !want.StartsWith("sha256:")) { Set("failed", null,
                    "GitHub published no SHA-256 for the zip: not installed"); return; }
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
                if (modules.Count == 0) return Api.Result(false, "check first: POST /update/check (no module channel known)");
                pick = modules.Where(m => (which == "all" || string.Equals(m.Name, which, StringComparison.OrdinalIgnoreCase))
                    && m.State == "available").ToList();
                if (pick.Count == 0)
                {
                    var m = modules.FirstOrDefault(x => string.Equals(x.Name, which, StringComparison.OrdinalIgnoreCase));
                    return Api.Result(false, which != "all" && m == null ? "no module " + which + " in the channel"
                        : m != null && m.State == "needs core" ? m.Name + " is built for a newer core: install the next LunarLink release first"
                        : "no newer module build");
                }
                foreach (var m in pick) { m.State = "downloading"; m.Error = null; }
                picked = pick;
            }
            Task.Run(ApplyModulesNow);
            return Json();
        }

        // the modules ApplyModulesNow downloads (handed over in a field: a lambda here needs a compiler attribute these
        // references lack)
        static List<Module> picked;
        static async Task ApplyModulesNow()
        {
            List<Module> pick; lock (gate) { pick = picked; picked = null; }
            var ready = new List<Module>();
            using var c = Client();
            foreach (var m in pick)
            {
                try
                {
                    if (string.IsNullOrEmpty(m.Sha256)
                        || string.IsNullOrEmpty(m.Url)) throw new InvalidOperationException("modules.json names no SHA-256 or address");
                    var dll = await c.GetByteArrayAsync(Fresh(m.Url));
                    if (Sha(dll) != m.Sha256.ToLowerInvariant()) throw new InvalidOperationException("the download doesn't " +
                        "match modules.json's SHA-256: not installed");
                    Put(Path.Combine(Hot.Dir, m.Name + ".dll"), dll);
                    lock (gate) m.State = "reloading";
                    ready.Add(m);
                }
                catch (Exception e) { lock (gate) { m.State = "failed"; m.Error = (e.InnerException ?? e).Message;
                    } Plugin.L.LogWarning("update: " + m.Name + ": " + m.Error); }
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
                try { using var d = JsonDocument.Parse(mf);
                    newHash = d.RootElement.TryGetProperty("coreHash", out var h) ? h.GetString() : null; }
                catch (System.Exception caught) { Quiet.Note(caught); }
            string oldHash = Manifest().core;
            bool changed = newHash == null || oldHash == null || newHash != oldHash;
            lock (gate) coreChanged = changed;

            if (!changed)
            {
                // same core: the modules into BepInEx/hot/ and reloaded (Tick); the core's files stay as they are,
                // installed-version too (it names the running core's build)
                foreach (var kv in mods) Put(Path.Combine(Hot.Dir, kv.Key), kv.Value);
                if (mf != null) Put(ManifestPath, mf);
                lock (gate) { toReload = mods.Keys.Select(Path.GetFileNameWithoutExtension).ToList(); state = "reloading";
                    detail = "reloading " + toReload.Count + " modules"; }
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
            Set("restart", "LunarLink " + latest
                + " is installed: restart the game to finish (the core loads only at start); it checks for newer module builds once it's back");
        }
    }
}
