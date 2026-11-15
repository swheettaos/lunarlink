using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;

namespace LunarLink
{
    // The fps fixes' settings in BepInEx/config/LunarLink.Perf.cfg, so F9 lists them (Hot Mods > Lunar Link Performance >
    // Settings) and anyone can edit the file. Each fix has its own on/off switch, and the ones with a value have that too.
    // Every change applies at once: the file is checked once a second and read again when it changed (F9 writes it),
    // and POST /perf/fixes/modify sets the same lines.
    // Older settings move in on the first start and their old form goes: power_fix.txt, offer_backoff.txt and
    // audio_far.txt next to the plugin (until 2026-11-15), and this file's first layout, where a value of 1 back-off
    // frame or 0 metres meant off.
    static class PerfSettings
    {
        static ConfigFile file;
        static ConfigEntry<bool> fastPower, backoffOn, silenceOn;
        static ConfigEntry<int> backoffFrames;
        static ConfigEntry<float> silenceMetres;
        static DateTime seen;
        static float nextLook;

        internal static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, PerfModule.Id + ".cfg");

        internal static void Load()
        {
            file = new ConfigFile(FilePath, true);
            fastPower = file.Bind("Power", "Fast power linking", true,
                "MoonFramework's once-a-second solar grid linking, done with remembered positions and cells (about 80 ms a run -> under 1 ms).");
            backoffOn = file.Bind("Belts", "Back off refused offers", false,
                "A capsule a full MoonFramework line refused waits a few frames before it's offered again, instead of every frame.");
            backoffFrames = file.Bind("Belts", "Back-off frames", 4,
                new ConfigDescription("How many frames a refused capsule waits.", new AcceptableValueRange<int>(2, 30)));
            silenceOn = file.Bind("Audio", "Silence far machines", true,
                "Machine sound loops far from you are stopped the game's own way (hundreds of solar trackers ran the game out of voices).");
            silenceMetres = file.Bind("Audio", "Silence beyond metres", 60f,
                new ConfigDescription("How far from you a machine has to be.", new AcceptableValueRange<float>(1f, 1000f)));
            Migrate();
            Apply();
            seen = Stamp();
        }

        // older forms of these settings, moved into their lines and then removed
        static void Migrate()
        {
            Old("power_fix.txt", v => fastPower.Value = v == "1");
            Old("offer_backoff.txt", v => Backoff(int.Parse(v)));
            Old("audio_far.txt", v => Silence(float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)));
            // this file's first layout: one value line each, 1 frame / 0 metres = off
            var orphans = typeof(ConfigFile).GetProperty("OrphanedEntries", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(file) as Dictionary<ConfigDefinition, string>;
            if (orphans == null || orphans.Count == 0) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var kv in new List<KeyValuePair<ConfigDefinition, string>>(orphans))
            {
                try
                {
                    if (kv.Key.Key == "Refused offer back-off frames") Backoff(int.Parse(kv.Value.Trim(), inv));
                    else if (kv.Key.Key == "Silent beyond metres") Silence(float.Parse(kv.Value.Trim(), inv));
                    else continue;
                }
                catch (Exception caught) { Quiet.Note(caught); }
                orphans.Remove(kv.Key);
                Plugin.L.LogInfo("perf: \"" + kv.Key.Key + "\" moved into its on/off and value lines");
            }
            file.Save();
        }

        static void Old(string name, Action<string> use)
        {
            string path = Path.Combine(Api.DataDir, name);
            if (!File.Exists(path)) return;
            try { use(File.ReadAllText(path).Trim()); } catch (Exception caught) { Quiet.Note(caught); }
            try { File.Delete(path); } catch (Exception caught) { Quiet.Note(caught); }
            Plugin.L.LogInfo("perf: " + name + " moved into " + Path.GetFileName(FilePath));
        }

        static DateTime Stamp() { try { return File.GetLastWriteTimeUtc(FilePath); } catch { return DateTime.MinValue; } }

        // once a second: the file changed on disk (F9, an editor) -> read it again and use it
        internal static void Update(float now)
        {
            if (file == null || now < nextLook) return;
            nextLook = now + 1f;
            var t = Stamp();
            if (t == seen) return;
            seen = t;
            try { file.Reload(); } catch (Exception e) { Plugin.L.LogWarning("perf: settings not read: " + e.Message); return; }
            Apply();
            Plugin.L.LogInfo("perf: settings changed: " + Describe());
        }

        static string Describe() => "fast power " + (fastPower.Value ? "on" : "off")
            + ", offer back-off " + (backoffOn.Value ? backoffFrames.Value + " frames" : "off")
            + ", far machines " + (silenceOn.Value ? "silent beyond " + silenceMetres.Value + " m" : "not silenced");

        static void Apply()
        {
            PowerFix.On = fastPower.Value;
            PowerFix.OfferBackoff = backoffOn.Value ? Math.Max(2, backoffFrames.Value) : 1;
            AudioFix.FarMetres = silenceOn.Value ? Math.Max(1f, silenceMetres.Value) : 0f;
        }

        // a back-off of N frames (1 or less: off) / silence beyond m metres (0 or less: off): the old one-number forms
        static void Backoff(int frames) { backoffOn.Value = frames > 1; if (frames > 1) backoffFrames.Value = Math.Min(30, frames); }
        static void Silence(float metres) { silenceOn.Value = metres > 0f; if (metres > 0f) silenceMetres.Value = Math.Min(1000f, metres); }

        // HTTP: the same lines, saved at once
        static void Saved() { Apply(); seen = Stamp(); }
        internal static void SetPower(bool on) { fastPower.Value = on; Saved(); }
        internal static void SetBackoffOn(bool on) { backoffOn.Value = on; Saved(); }
        internal static void SetBackoff(int frames) { Backoff(frames); Saved(); }
        internal static void SetSilenceOn(bool on) { silenceOn.Value = on; Saved(); }
        internal static void SetSilence(float metres) { Silence(metres); Saved(); }
    }
}
