using System;
using HarmonyLib;

namespace LunarLink
{
    // Measuring and fps fixes. Every Harmony patch this module makes is under an id starting with "LunarLink.Perf.",
    // and Stop() takes them all out, so turning the module off (or reloading it) removes every timer and fix it put in.
    //   fixes: PowerFix (a faster PowerSystem.Link, and refused capsule offers backed off), AudioFix (machine sound loops
    //   off beyond a distance). Their settings are files in BepInEx/plugins/LunarLink/.
    public class PerfModule
    {
        internal const string Id = "LunarLink.Perf";

        public void Start(string state)
        {
            Api.Get("/perf", "fps and ms per frame of the timed systems since the last read (read once, wait, read again); with the engine counters on, Unity's own (engineMs)", r => Read());
            Api.Post("/perf/engine", "?on=1|0 : Unity's own frame counters (frame, scripts, physics, rendering, GPU wait, draw calls) on or off", r => Engine(r.Query["on"] == "1"));
            Api.Post("/perf/timers", "Stopwatches on the game's and MoonFramework's main systems, read through /perf. They cost frame time: reload or disable the module to take them out", r => Timers());
            Api.Post("/perf/time", "?type=Full.Name&method=Name/paramCount : a stopwatch on one method, read through /perf", r => TimeOne(r));
            Api.Get("/perf/methods", "?type=X : the methods a class declares, to pick what to time", r => Perf.Methods(r.Query["type"] ?? ""));
            Api.Get("/census", "?top= : every component class counted, with which have per-frame methods", r => Census.Json(Api.Qi(r, "top", 40)));
            Api.Get("/perf/fixes", "The fps fixes: power (fast link on, offer back-off frames) and audio (silent beyond m metres), with their counters", r => Fixes());
            Api.Post("/perf/fixes/modify", "?name=power&on=1|0 / ?name=power&backoff=N / ?name=audio&m=N (0 = off) : change a fix and keep the setting", r => Modify(r));
        }

        public string Stop()
        {
            EngineTimers.Stop();
            Harmony.UnpatchID(Id + ".power"); Harmony.UnpatchID(Id + ".offers"); Harmony.UnpatchID(Id + ".audio");
            Harmony.UnpatchID(Id + ".timers"); Harmony.UnpatchID(Id + ".time");
            return null;
        }

        // the fixes go in once the game has loaded (the module starts then), each only when its setting asks for it
        public void Update()
        {
            EngineTimers.Sample();
            Perf.Frame();
            if (!PowerFix.Installed && PowerFix.On) PowerFix.Install();
            if (PowerFix.OfferBackoff > 1) PowerFix.InstallOffer();
            if (!AudioFix.Installed && AudioFix.FarMetres > 0f) AudioFix.Install();
        }

        static string Read()
        {
            var pj = Perf.Json();
            return EngineTimers.Running ? pj.Substring(0, pj.Length - 1) + ",\"engineMs\":" + EngineTimers.Json() + "}" : pj;
        }

        static string Engine(bool on)
        {
            if (on) { try { EngineTimers.Start(); } catch (Exception e) { return "{\"ok\":false,\"reason\":" + J.S(e.Message) + "}"; } }
            else EngineTimers.Stop();
            return "{\"ok\":true,\"engine\":" + (EngineTimers.Running ? "true" : "false") + "}";
        }

        static string Timers()
        {
            if (Perf.Main) return "{\"ok\":true,\"installed\":" + J.S(Perf.Installed) + "}";
            Pump.Journal("perf: installing timers on request");
            try { Perf.Install(new Harmony(Id + ".timers")); } catch (Exception e) { Pump.Journal("perf: timers failed: " + e.Message); return "{\"ok\":false,\"reason\":" + J.S(e.Message) + "}"; }
            Pump.Journal("perf: timers installed: " + Perf.Installed);
            return "{\"ok\":true,\"installed\":" + J.S(Perf.Installed) + "}";
        }

        static string TimeOne(Req r)
        {
            try { return Perf.TimeOne(new Harmony(Id + ".time"), r.Query["type"] ?? "", r.Query["method"] ?? "Update"); }
            catch (Exception e) { return "{\"ok\":false,\"reason\":" + J.S(e.Message) + "}"; }
        }

        static string Fixes() => "{\"power\":" + PowerFix.Json() + ",\"audio\":" + AudioFix.Json() + "}";

        static string Modify(Req r)
        {
            switch (r.Query["name"])
            {
                case "power":
                    if (r.Query["backoff"] != null) PowerFix.SetBackoff(Api.Qi(r, "backoff", 1));
                    if (r.Query["on"] != null) PowerFix.Set(r.Query["on"] == "1");
                    return "{\"ok\":true,\"power\":" + PowerFix.Json() + "}";
                case "audio":
                    if (r.Query["m"] != null) AudioFix.Set(Api.Qf(r, "m", 60f));
                    return "{\"ok\":true,\"audio\":" + AudioFix.Json() + "}";
            }
            return "{\"ok\":false,\"reason\":\"name=power or name=audio\"}";
        }
    }
}
