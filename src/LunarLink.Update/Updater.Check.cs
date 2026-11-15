using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace LunarLink
{
    // Asking GitHub: the latest release, the module channel (modules.json) and what each module's build would change.
    static partial class Updater
    {
        // a new core waiting for the restart: nothing is checked or installed until it's done (on top of a pending core,
        // a new release or module could be meant for either core)
        const string RestartFirst = "a new LunarLink core is installed: restart the game first (the core loads only at start)";

        internal static string Check()
        {
            if (Busy) return Json();
            if (RestartPending) { Set("restart", "LunarLink " + Running + " is installed, but the game still runs " + Loaded
                + ": restart the game to finish (the core loads only at start)"); return Json(); }
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
                if (RestartPending)
                {
                    Set("restart", "LunarLink " + Running + " is installed, but the game still runs " + Loaded
                        + ": restart the game to finish (the core loads only at start)");
                    return;
                }
                bool newer = Version.TryParse(ver, out var v) && Version.TryParse(Running, out var mine) && v > mine;
                Set(newer ? "available" : "up to date", newer ? "LunarLink " + ver + " is available (running " + Running + ")"
                    : "LunarLink " + Running + " is the latest release");
            }
            catch (Exception e) { Set("failed", null, "check: " + (e.InnerException ?? e).Message); }
        }

        // modules.json from the module channel: each module's state against what's installed
        static async Task CheckChannel(HttpClient c)
        {
            try
            {
                var res = await c.GetAsync(Fresh(Channel));
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound) { lock (gate) { modules.Clear(); } return; }   // no channel yet
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync()); var r = doc.RootElement;
                var installed = Manifest(); var list = new List<Module>();
                string core = r.GetProperty("coreHash").GetString();
                foreach (var p in r.GetProperty("modules").EnumerateObject())
                {
                    var e = p.Value; string Str(string k) => e.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                    var m = new Module { Name = p.Name, Version = Str("version"), SourceHash = Str("sourceHash"),
                        Sha256 = Str("sha256"), Url = Str("url"), Note = Str("note"),
                                         Built = Str("built"), Commit = Str("commit"), Size = e.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0 };
                    // available: other source, or the same source with a newer number (a module built before versions has none)
                    m.State = core != installed.core ? "needs core"
                            : installed.modules.TryGetValue(m.Name, out var have) && have == m.SourceHash
                                && !Newer(m.Version, Installed(m.Name)) ? "up to date" : "available";
                    list.Add(m);
                }
                lock (gate) { modules.Clear(); modules.AddRange(list.OrderBy(m => m.Name));
                    channelModified = res.Content.Headers.LastModified?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"; }
            }
            catch (Exception e) { lock (gate) channelError = "module channel: " + (e.InnerException ?? e).Message;
                Plugin.L.LogWarning("update: module channel: " + (e.InnerException ?? e).Message); }
        }
    }
}
