using System;
using System.IO;
using BepInEx.Configuration;

namespace LunarLink
{
    // MoonFramework's save files, guarded (SaveGuard.cs) - only while MoonFramework is loaded. Without it this module does
    // nothing at all: no patch, no setting, no endpoint. Its endpoints are left out of LunarLink's documentation on
    // purpose, and they're not added when MoonFramework answers requests of its own (a later MoonFramework may bring its
    // own way to look after these files).
    public class FrameworkModule
    {
        internal const string Id = "LunarLink.Framework";
        static ConfigFile file; static ConfigEntry<bool> guard; static DateTime seen;
        static float nextLook;

        // at game start, before the first world loads: the guard has to be in before MoonFramework reads a save
        public static void Boot() => Begin();

        public void Start(string state)
        {
            if (!Begin()) return;
            if (FrameworkAnswersRequests()) return;
            Api.Get("/framework/saves", "MoonFramework's save file in each slot, the copies kept of it, and what the guard did", r => SaveGuard.Json());
            Api.Post("/framework/saves/restore", "?slot=N[&copy=name] : put a slot's copy back (the newest by default); not for the loaded slot",
                r => SaveGuard.RestoreByHand(Api.Qi(r, "slot", -1), r.Query["copy"]));
        }

        public string Stop() { SaveGuard.Uninstall(); return null; }

        public void Update()
        {
            float now = UnityEngine.Time.unscaledTime;
            SaveGuard.Update(now);
            if (file == null || now < nextLook) return;
            nextLook = now + 1f;                                        // its setting, changed in F9 or the file: in use at once
            var t = Stamp(); if (t == seen) return;
            seen = t;
            try { file.Reload(); SaveGuard.On = guard.Value; } catch (Exception caught) { Quiet.Note(caught); }
        }

        static bool Begin()
        {
            if (!SaveGuard.Install()) return false;
            if (file == null)
            {
                file = new ConfigFile(Path.Combine(BepInEx.Paths.ConfigPath, Id + ".cfg"), true);
                guard = file.Bind("Safety", "Guard MoonFramework saves", true,
                    "Keep MoonFramework from writing a save slot's file from another world, keep copies of each slot's file, and put "
                    + "a wiped one back as its world loads.");
                SaveGuard.On = guard.Value; seen = Stamp();
            }
            return true;
        }

        static DateTime Stamp() { try { return File.GetLastWriteTimeUtc(Path.Combine(BepInEx.Paths.ConfigPath, Id + ".cfg")); } catch { return DateTime.MinValue; } }

        // a MoonFramework that answers requests itself (LunarLink's HandleHttp convention) has its own way: ours steps aside
        static bool FrameworkAnswersRequests()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "MoonFramework") continue;
                Type[] types;
                try { types = a.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var t in types) if (t != null && t.GetMethod("HandleHttp") != null) return true;
            }
            return false;
        }
    }
}
