// Normal BepInEx plugins (in BepInEx/plugins/, loaded at game start): turned off or on for the NEXT start. A loaded
// plugin can't be unloaded, so "disable" renames its DLL to .dll.disabled (BepInEx only loads *.dll) and it keeps running
// until the game restarts; "enable" renames it back. A plugin another enabled plugin needs (a hard BepInDependency) can't
// be turned off, and LunarLink itself has no switch (it runs this).
//   GET /plugins                     every plugin: name, guid, version, file, state, what needs it
//   POST /plugins/disable?file=      file as /plugins lists it, relative to BepInEx/plugins/
//   POST /plugins/enable?file=
// Part of the LunarLink.Window module (not the core), so it changes without a core update: the /plugins endpoints
// come and go with the window module. The plugins turned on this session (enabledNow) go to the module's next copy
// through its Stop/Start state.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace LunarLink
{
    // The normal BepInEx plugins: listed, and turned on or off for the next game start.
    static class BepPlugins
    {
        const string Off = ".disabled";

        internal sealed class Entry
        {
            public string Name, Guid, Version, File, State; public bool Loaded, Self; public List<string> NeededBy = new List<string>();
        }

        static string Rel(string path) => Path.GetRelativePath(Paths.PluginPath, path).Replace('\\', '/');
        static string Abs(string file) => Path.GetFullPath(Path.Combine(Paths.PluginPath, file));

        // Every loaded plugin, plus every .dll.disabled under plugins/ that isn't one of them.
        internal static List<Entry> List()
        {
            var list = new List<Entry>();
            var loaded = IL2CPPChainloader.Instance.Plugins.Values.ToList();
            foreach (var p in loaded)
            {
                var file = Rel(p.Location);
                var e = new Entry { Name = p.Metadata.Name, Guid = p.Metadata.GUID, Version = p.Metadata.Version?.ToString(), File = file, Loaded = true,
                                    Self = p.Metadata.GUID == "lunarlink" };
                e.State = System.IO.File.Exists(p.Location) ? "running" : System.IO.File.Exists(p.Location + Off) ? "off from next start" : "file gone";
                foreach (var q in loaded)
                    if (q != p && System.IO.File.Exists(q.Location)
                        && q.Dependencies.Any(d => d.DependencyGUID == p.Metadata.GUID && (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0))
                        e.NeededBy.Add(q.Metadata.Name);
                list.Add(e);
            }
            var known = new HashSet<string>(list.Select(e => e.File), StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(Paths.PluginPath, "*.dll" + Off, SearchOption.AllDirectories))
            {
                var file = Rel(path.Substring(0, path.Length - Off.Length));
                if (known.Contains(file)) continue;
                list.Add(new Entry { Name = Path.GetFileNameWithoutExtension(file), File = file, State = "off" });
            }
            foreach (var file in enabledNow)
                if (!known.Contains(file) && System.IO.File.Exists(Abs(file)))
                    list.Add(new Entry { Name = Path.GetFileNameWithoutExtension(file), File = file, State = "on from next start" });
            return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static readonly HashSet<string> enabledNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // turned on this session

        // what a reload of the window module hands over: the files turned on this session, one a line
        internal static string Handover() => string.Join("\n", enabledNow);
        internal static void TakeOver(string state)
        {
            enabledNow.Clear();
            foreach (var f in (state ?? "").Split('\n')) if (f.Length > 0 && !f.StartsWith("#")) enabledNow.Add(f);
        }

        // Returns null when done, or why not.
        internal static string Disable(string file)
        {
            var e = List().FirstOrDefault(x => string.Equals(x.File, file, StringComparison.OrdinalIgnoreCase));
            if (e == null) return "no plugin " + file;
            if (e.Self) return "LunarLink runs this window and the API; it can't turn itself off";
            if (e.NeededBy.Count > 0) return e.Name + " is needed by " + string.Join(", ", e.NeededBy) + ": turn those off first";
            var path = Abs(e.File);
            if (!System.IO.File.Exists(path)) return e.Name + " is already off";
            System.IO.File.Move(path, path + Off);
            enabledNow.Remove(e.File);
            Plugin.L.LogInfo("plugins: " + e.File + " off from the next start");
            return null;
        }

        internal static string Enable(string file)
        {
            // the same plugin running hot from BepInEx/hot/ would load twice at the next start
            var stem = Path.GetFileNameWithoutExtension(file);
            if (Hot.Find(stem) is Hot.Plugin hot && hot.Enabled)
                return stem + " runs hot from BepInEx/hot/: turning this copy on too would load it twice. Disable the hot one first (HOT PLUGINS) to use this copy";
            var path = Abs(file);
            if (System.IO.File.Exists(path)) return Path.GetFileName(file) + " is already on";
            if (!System.IO.File.Exists(path + Off)) return "no " + file + Off;
            System.IO.File.Move(path + Off, path);
            enabledNow.Add(Rel(path));
            Plugin.L.LogInfo("plugins: " + file + " on from the next start");
            return null;
        }

        internal static void Routes()
        {
            Api.Get("/plugins", "The normal BepInEx plugins: name, guid, version, file, state, what needs them", r =>
                "{\"plugins\":[" + string.Join(",", List().Select(e => "{\"name\":" + J.S(e.Name) + ",\"guid\":" + J.S(e.Guid) + ",\"version\":" + J.S(e.Version)
                    + ",\"file\":" + J.S(e.File) + ",\"state\":" + J.S(e.State) + ",\"neededBy\":[" + string.Join(",", e.NeededBy.Select(J.S)) + "]}")) + "]}");
            Api.Post("/plugins/disable", "?file= : off from the next game start (restart required)", r => Change(r.Query["file"], Disable));
            Api.Post("/plugins/enable", "?file= : on from the next game start (restart required)", r => Change(r.Query["file"], Enable));
        }

        static string Change(string file, Func<string, string> act)
        {
            if (string.IsNullOrEmpty(file)) return "{\"error\":\"file= is required (as GET /plugins lists it)\"}";
            string why;
            try { why = act(file); }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }
            if (why != null) return "{\"ok\":false,\"reason\":" + J.S(why) + "}";
            var now = List().FirstOrDefault(x => string.Equals(x.File, file, StringComparison.OrdinalIgnoreCase));
            bool restart = now != null && now.State.Contains("next start");          // back to how it started: nothing to restart for
            return "{\"ok\":true,\"state\":" + J.S(now?.State) + ",\"restartRequired\":" + (restart ? "true" : "false")
                + ",\"note\":" + J.S(restart ? "restart the game to apply" : "back to how the game started it: no restart needed") + "}";
        }
    }
}
