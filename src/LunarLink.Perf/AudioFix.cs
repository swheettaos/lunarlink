using System;
using System.Reflection;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    // Hundreds of solar trackers each play a looping servo sound while they turn; the game's audio ran out of voices
    // ("Ran out of virtual channels. Audio clip solar-servo-loop will not be played") and logged a stack trace each
    // time - 2671 in one session (2026-11-07). MoonProductionView.Loop(ref source, clip, on, volume, distance) is the
    // game's own start/stop of a machine's loop: for machines further than FarMetres from the listener it is called
    // with on = false, so the game stops that sound the normal way. POST /perf/fixes/modify?name=audio&m=N (0 = off), kept in audio_far.txt.
    static class AudioFix
    {
        public static float FarMetres = Load(); public static bool Installed; public static string Reason;
        public static long Silenced, Passed;
        static int listenerFrame = -1; static Vector3 ear; static bool hasEar;
        static string Path => System.IO.Path.Combine(Api.DataDir, "audio_far.txt");
        static float Load() { try { return Math.Max(0f, float.Parse(System.IO.File.ReadAllText(Path).Trim(), System.Globalization.CultureInfo.InvariantCulture)); } catch { return 60f; } }

        public static string Set(float m)
        {
            FarMetres = Math.Max(0f, m);
            try { System.IO.File.WriteAllText(Path, FarMetres.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
            if (!Installed) Install();
            return Json();
        }

        public static void Install()
        {
            if (Installed) return; Installed = true;
            try
            {
                var m = typeof(MoonProductionView).GetMethod("Loop", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null) throw new Exception("no MoonProductionView.Loop");
                Pump.Journal("audio: patching MoonProductionView.Loop");
                new Harmony(PerfModule.Id + ".audio").Patch(m, prefix: new HarmonyMethod(typeof(AudioFix).GetMethod(nameof(LoopPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Pump.Journal("audio: patched, machines beyond " + FarMetres + " m stay silent");
            }
            catch (Exception e) { Reason = e.Message; Pump.Journal("audio: patch failed: " + e.Message); }
        }

        static void LoopPrefix(MoonProductionView __instance, ref bool __2)
        {
            if (!__2 || FarMetres <= 0f || Reason != null) { Passed++; return; }
            try
            {
                int f = Time.frameCount;
                if (f != listenerFrame)
                {
                    // the main camera carries the listener (FindObjectOfType<AudioListener> is stripped from the game's
                    // build: "Method unstripping failed", 2026-11-07); no camera = leave the sound alone
                    listenerFrame = f; var cam = Camera.main; hasEar = cam != null;
                    if (hasEar) ear = cam.transform.position;
                }
                if (hasEar && (__instance.transform.position - ear).sqrMagnitude > FarMetres * FarMetres) { __2 = false; Silenced++; }
                else Passed++;
            }
            catch (Exception e) { Reason = e.Message; Pump.Journal("audio: switched off after an error: " + e.Message); }
        }

        public static string Json() =>
            "{\"ok\":" + (Reason == null ? "true" : "false") + ",\"farMetres\":" + J.F(FarMetres) + ",\"installed\":" + (Installed ? "true" : "false")
            + ",\"silenced\":" + Silenced + ",\"passed\":" + Passed + ",\"reason\":" + J.S(Reason) + "}";
    }
}
