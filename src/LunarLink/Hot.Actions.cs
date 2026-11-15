using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text;
using HarmonyLib;

namespace LunarLink
{
    // What the API and the F9 window do to a plugin: enable, disable, reload (now, or at the main menu for a wrapped one
    // that loads content), and the rows F9 lists.
    static partial class Hot
    {
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
        internal static Plugin CancelReload(string name) { var p = Find(name);
            if (p != null && p.ReloadAtMenu) { p.ReloadAtMenu = false; Log("reload of " + p.Name + " cancelled"); } return p; }

        // ReloadWatch: a just-reloaded plugin kept throwing. Stopped (it stays on, so the next start loads it cleanly),
        // unless it can't be stopped safely (native hooks): then it's only marked.
        internal static void StopFailed(string name, int errors)
        {
            if (!plugins.TryGetValue(name, out var p)) return;
            bool stop = p.Obj != null && !Pinned(p);
            if (stop) StopAndUnload(p);
            p.Error = "its new copy failed " + errors + " times in its first seconds" + (stop ? ", so it was stopped" : "")
                + ": restart the game to load it cleanly";
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
            foreach (var p in done.Where(Waits).ToList()) { done.Remove(p);
                if (!p.ReloadAtMenu) { p.ReloadAtMenu = true; Log("reload of " + p.Name + " waits for the main menu"); } }
            foreach (var p in done) Reload(p);
            Bridges.Purge();
            return done;
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
    }
}
