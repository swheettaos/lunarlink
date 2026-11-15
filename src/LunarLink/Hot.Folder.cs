using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text;
using HarmonyLib;

namespace LunarLink
{
    // The folder and its list: hot.json (which plugins are on), the DLLs in BepInEx/hot/, and picking up ones added or removed.
    static partial class Hot
    {
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
            var mine = DllNames().Where(n => n.StartsWith("LunarLink.", StringComparison.OrdinalIgnoreCase)
                && !enabled.Contains(n) && !disabled.Contains(n)).ToList();
            foreach (var name in mine) enabled.Add(name);
            if (mine.Count > 0) Save();
            Rescan();
            // LunarLink's own modules first: the wrapper module has to be in place before a plugin it wraps loads
            foreach (var p in plugins.Values.Where(p => p.Enabled
                && File.Exists(FileOf(p.Name))).OrderBy(p => p.Name.StartsWith("LunarLink.", StringComparison.OrdinalIgnoreCase)
                ? 0 : 1).ToList())
            {
                try { Load(p); if (p.HasBoot) { Call(p, "Boot"); p.Booted = true; } }
                catch (Exception e) { Fail(p, "load", e); }
            }
        }

        static IEnumerable<string> DllNames() => Directory.Exists(Dir)
            ? Directory.GetFiles(Dir, "*.dll").Select(Path.GetFileNameWithoutExtension)
                .Concat(Directory.GetDirectories(Dir).Select(Path.GetFileName).Where(d => File.Exists(Path.Combine(Dir, d, d + ".dll"))))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n) : Enumerable.Empty<string>();

        static void Save()
        {
            var set = new Settings { enabled = enabled.OrderBy(n => n).ToList(), disabled = disabled.OrderBy(n => n).ToList() };
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsFile
                + ".new", System.Text.Json.JsonSerializer.Serialize(set,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
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

        internal static (List<string> added, List<string> removed) RescanAll()
        {
            var r = Rescan();
            Bridges.Purge();
            return r;
        }
    }
}
