// Plugins that update themselves. A plugin, hot or normal, says where its builds are with a public method
//   string UpdateFeed()
// on the class LunarLink starts (a hot plugin) or on its BepInPlugin class (a normal one), found by name, so it needs no
// reference to LunarLink. It answers one of:
//   "github:owner/repo"                    the repo's latest GitHub release: its version is the tag (v1.2.3), its notes
//                                          the release's text, and its file the release's .zip (or a .dll named like
//                                          the plugin's), checked against the SHA-256 GitHub publishes
//   "https://example.com/myplugin.json"    any public HTTPS address of an update file:
//                                            {"version": "1.2.3", "notes": "...", "url": "https://.../MyPlugin.zip", "sha256": "..."}
//                                          or, for a plugin of several DLLs (modules), a file each:
//                                            {"version": "1.2.3", "notes": "...", "files": [{"name": "MyPlugin.dll", "url": "...", "sha256": "..."}, ...]}
//   null                                   no updates
// Checking happens with LunarLink's own check (when asked, or at game start if that's on). Installing takes only DLLs that are the plugin's own:
// its DLL's name, or names starting with "<its name>." (MyPlugin.Extra.dll), from the folder in the zip that holds its
// DLL. A hot plugin's go into BepInEx/hot/ and it reloads in place; a normal plugin's DLL is swapped like LunarLink's
// core (the loaded file renamed to .old, the new one in its place) and the game needs a restart.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace LunarLink
{
    static class PluginUpdates
    {
        internal sealed class Entry
        {
            public string Name, Kind, Version, Feed, Host, Latest, Notes, State, Error, Page;
            public string Dir, Dll;                                  // where its DLL lives, and its file name
            public string ZipUrl, ZipSha;                            // a zip to take its DLLs from, or
            public List<(string name, string url, string sha)> Files = new List<(string, string, string)>();
        }

        static readonly object gate = new object();
        static readonly List<Entry> entries = new List<Entry>();
        static List<Entry> toReload;                                 // hot plugins updated, to reload on the main thread
        static Entry picked;                                         // the one ApplyNow installs

        internal static bool Busy { get { lock (gate) return entries.Any(e => e.State == "checking" || e.State == "downloading" || e.State == "reloading"); } }

        internal static string Json()
        {
            lock (gate)
                return "[" + string.Join(",", entries.Select(e => "{\"name\":" + J.S(e.Name) + ",\"kind\":" + J.S(e.Kind) + ",\"version\":" + J.S(e.Version)
                    + ",\"latest\":" + J.S(e.Latest) + ",\"state\":" + J.S(e.State) + ",\"from\":" + J.S(e.Host) + ",\"page\":" + J.S(e.Page)
                    + ",\"notes\":" + J.S(e.Notes) + ",\"error\":" + J.S(e.Error) + "}")) + "]";
        }

        // On the main thread (a request or a frame): every plugin that answers UpdateFeed(), with its version and files.
        internal static void Discover()
        {
            var found = new List<Entry>();
            foreach (var (name, obj) in Hot.Loaded())
            {
                if (name.StartsWith("LunarLink.", StringComparison.OrdinalIgnoreCase)) continue;      // LunarLink's own: the module channel
                var feed = Feed(obj); if (feed == null) continue;
                found.Add(new Entry { Name = name, Kind = "hot", Version = VersionOf(obj.GetType().Assembly), Feed = feed, Dir = System.IO.Path.GetDirectoryName(Hot.FileOf(name)), Dll = name + ".dll" });
            }
            foreach (var info in IL2CPPChainloader.Instance.Plugins.Values)
            {
                if (info.Instance == null || info.Metadata.GUID == "lunarlink") continue;
                var feed = Feed(info.Instance); if (feed == null) continue;
                found.Add(new Entry { Name = info.Metadata.Name, Kind = "plugin", Version = info.Metadata.Version.ToString(), Feed = feed,
                                      Dir = Path.GetDirectoryName(info.Location), Dll = Path.GetFileName(info.Location) });
            }
            foreach (var e in found) { e.State = "checking"; e.Host = HostOf(e.Feed); }
            lock (gate) { entries.Clear(); entries.AddRange(found.OrderBy(e => e.Name)); }
        }

        static string Feed(object obj)
        {
            try
            {
                var m = obj.GetType().GetMethod("UpdateFeed", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return m != null && m.ReturnType == typeof(string) ? ((string)m.Invoke(obj, null))?.Trim() : null;
            }
            catch (Exception e) { Plugin.L.LogWarning("update: " + obj.GetType().Name + ".UpdateFeed: " + (e.InnerException ?? e).Message); return null; }
        }

        // a hot plugin's version: the [BepInPlugin] in its DLL (the class its normal build loads), else the assembly's
        static string VersionOf(Assembly a)
        {
            try
            {
                foreach (var t in a.GetTypes())
                    if (t.GetCustomAttribute<BepInPlugin>() is BepInPlugin bp) return bp.Version.ToString();
            }
            catch { }
            return a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version?.ToString();
        }

        static string HostOf(string feed) => feed.StartsWith("github:") ? "github.com/" + feed.Substring(7)
            : Uri.TryCreate(feed, UriKind.Absolute, out var u) ? u.Host : feed;

        static bool Newer(string latest, string have)
        {
            static Version V(string s) { s = (s ?? "").TrimStart('v'); int cut = s.IndexOfAny(new[] { '-', '+', ' ' }); if (cut > 0) s = s.Substring(0, cut); return Version.TryParse(s, out var v) ? v : null; }
            var a = V(latest); var b = V(have);
            return a != null && (b == null || a > b);
        }

        // In the background, with LunarLink's own check: each plugin's feed.
        internal static async Task CheckAll(HttpClient c)
        {
            List<Entry> list; lock (gate) list = entries.ToList();
            foreach (var e in list)
            {
                try
                {
                    if (e.Feed.StartsWith("github:")) await GitHub(c, e);
                    else if (e.Feed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) await JsonFeed(c, e);
                    else throw new InvalidOperationException("UpdateFeed() must answer github:owner/repo or an https:// address, not \"" + e.Feed + "\"");
                    lock (gate) e.State = Newer(e.Latest, e.Version) ? "available" : "up to date";
                }
                catch (Exception x) { lock (gate) { e.State = "failed"; e.Error = (x.InnerException ?? x).Message; } }
            }
        }

        static async Task GitHub(HttpClient c, Entry e)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + e.Feed.Substring(7).Trim('/') + "/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            var res = await c.SendAsync(req); res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync()); var r = doc.RootElement;
            string latest = (r.GetProperty("tag_name").GetString() ?? "").TrimStart('v'), notes = r.TryGetProperty("body", out var b) ? b.GetString() : null;
            string zip = null, zipSha = null; var files = new List<(string, string, string)>();
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                string n = a.GetProperty("name").GetString() ?? "", url = a.GetProperty("browser_download_url").GetString();
                string sha = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString().StartsWith("sha256:") ? d.GetString().Substring(7) : null;
                if (zip == null && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zip = url; zipSha = sha; }
                else if (Own(e, n)) files.Add((n, url, sha));
            }
            lock (gate) { e.Latest = latest; e.Notes = notes; e.Page = r.GetProperty("html_url").GetString(); e.ZipUrl = zip; e.ZipSha = zipSha; e.Files = files; }
            if (zip == null && files.Count == 0) throw new InvalidOperationException("release " + latest + " has no zip and no " + e.Dll);
        }

        static async Task JsonFeed(HttpClient c, Entry e)
        {
            using var doc = JsonDocument.Parse(await c.GetStringAsync(e.Feed)); var r = doc.RootElement;
            string S(JsonElement x, string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var files = new List<(string, string, string)>(); string zip = null, zipSha = null;
            if (r.TryGetProperty("files", out var fs))
                foreach (var f in fs.EnumerateArray()) files.Add((S(f, "name"), S(f, "url"), S(f, "sha256")));
            else
            {
                var url = S(r, "url"); var name = Path.GetFileName(new Uri(url).AbsolutePath);
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zip = url; zipSha = S(r, "sha256"); } else files.Add((name, url, S(r, "sha256")));
            }
            lock (gate) { e.Latest = S(r, "version"); e.Notes = S(r, "notes"); e.Page = S(r, "page"); e.ZipUrl = zip; e.ZipSha = zipSha; e.Files = files; }
            if (e.Latest == null) throw new InvalidOperationException("the update file has no \"version\"");
        }

        // a DLL this plugin may write: its own name, or one starting with "<its name>."
        static bool Own(Entry e, string file)
        {
            var stem = Path.GetFileNameWithoutExtension(e.Dll);
            return file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(file) == file
                && (string.Equals(file, e.Dll, StringComparison.OrdinalIgnoreCase) || file.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase));
        }

        internal static string Apply(string name)
        {
            lock (gate)
            {
                var e = entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (e == null) return "{\"ok\":false,\"reason\":" + J.S("no plugin " + name + " with UpdateFeed() (check first: POST /update/check)") + "}";
                if (e.State != "available") return "{\"ok\":false,\"reason\":" + J.S(name + " has no newer version") + "}";
                e.State = "downloading"; e.Error = null; picked = e;
            }
            Task.Run(ApplyNow);
            return Updater.Json();
        }

        static async Task ApplyNow()
        {
            Entry e; lock (gate) { e = picked; picked = null; }
            try
            {
                var dlls = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                c.DefaultRequestHeaders.UserAgent.ParseAdd("LunarLink-updater");
                if (e.ZipUrl != null)
                {
                    var zip = await Get(c, e.ZipUrl, e.ZipSha);
                    using var z = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
                    var mine = z.Entries.FirstOrDefault(x => string.Equals(Path.GetFileName(x.FullName), e.Dll, StringComparison.OrdinalIgnoreCase));
                    if (mine == null) throw new InvalidOperationException("the zip has no " + e.Dll);
                    var folder = Path.GetDirectoryName(mine.FullName.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
                    foreach (var x in z.Entries)
                    {
                        var n = Path.GetFileName(x.FullName);
                        if ((Path.GetDirectoryName(x.FullName.Replace('\\', '/'))?.Replace('\\', '/') ?? "") != folder || !Own(e, n)) continue;
                        var ms = new MemoryStream(); using (var s = x.Open()) s.CopyTo(ms); dlls[n] = ms.ToArray();
                    }
                }
                foreach (var (n, url, sha) in e.Files)
                {
                    if (!Own(e, n)) throw new InvalidOperationException(n + " isn't one of " + e.Name + "'s own files");
                    dlls[n] = await Get(c, url, sha);
                }
                if (!dlls.ContainsKey(e.Dll)) throw new InvalidOperationException("the update has no " + e.Dll);
                if (e.Kind == "hot")
                {
                    foreach (var kv in dlls) Put(Path.Combine(e.Dir, kv.Key), kv.Value);
                    lock (gate) { e.State = "reloading"; (toReload ??= new List<Entry>()).Add(e); }
                }
                else
                {
                    foreach (var kv in dlls)
                    {
                        var path = Path.Combine(e.Dir, kv.Key);
                        File.WriteAllBytes(path + ".new", kv.Value);
                        if (File.Exists(path)) { if (File.Exists(path + ".old")) File.Delete(path + ".old"); File.Move(path, path + ".old"); }   // a loaded DLL can be renamed
                        File.Move(path + ".new", path);
                    }
                    lock (gate) e.State = "restart";
                }
                Plugin.L.LogInfo("update: " + e.Name + " " + e.Latest + " installed from " + e.Host + (e.Kind == "hot" ? "" : " (restart to finish)"));
            }
            catch (Exception x) { lock (gate) { e.State = "failed"; e.Error = (x.InnerException ?? x).Message; } Plugin.L.LogWarning("update: " + e.Name + ": " + e.Error); }
        }

        // https only, and the SHA-256 must match
        static async Task<byte[]> Get(HttpClient c, string url, string sha)
        {
            if (url == null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("only https:// downloads: " + url);
            if (string.IsNullOrEmpty(sha)) throw new InvalidOperationException("no SHA-256 given for " + url + ": not installed");
            var b = await c.GetByteArrayAsync(url);
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant() != sha.ToLowerInvariant())
                throw new InvalidOperationException("the download doesn't match its SHA-256: not installed");
            return b;
        }

        static void Put(string path, byte[] bytes)
        {
            File.WriteAllBytes(path + ".new", bytes);
            File.Move(path + ".new", path, true);
        }

        // Every frame (the module's Update): reload the hot plugins an update put in place.
        internal static void Tick()
        {
            List<Entry> list; lock (gate) { list = toReload; toReload = null; }
            if (list == null) return;
            foreach (var e in list)
            {
                var p = Hot.Find(e.Name);
                bool ok = p == null || !p.Enabled || (Hot.Enable(e.Name, reload: true)?.Error == null);
                lock (gate) { e.State = ok ? "updated" : "failed"; if (ok) e.Version = e.Latest; else e.Error = "it didn't start: see F9's HOT PLUGINS tab"; }
            }
        }
    }
}
