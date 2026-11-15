using System;
using HarmonyLib;

namespace LunarLink
{
    // Measuring and fps fixes. Every Harmony patch this module makes is under an id starting with "LunarLink.Perf.",
    // and Stop() takes them all out, so turning the module off (or reloading it) removes every timer and fix it put in.
    //   fixes: PowerFix (a faster PowerSystem.Link, and refused capsule offers backed off), AudioFix (machine sound loops
    //   off beyond a distance). Their settings: BepInEx/config/LunarLink.Perf.cfg (PerfSettings), changed live.
    public class PerfModule
    {
        internal const string Id = "LunarLink.Perf";

        public void Start(string state)
        {
            PerfSettings.Load();
            Api.Get("/perf", "fps and ms per frame of the timed systems since the last read (read once, wait, read again); " +
                "with the engine counters on, Unity's own (engineMs)", r => Read());
            Api.Post("/perf/engine",
                "?on=1|0 : Unity's own frame counters (frame, scripts, physics, rendering, GPU wait, draw calls) on or off",
                r => Engine(r.Query["on"] == "1"));
            Api.Post("/perf/timers",
                "Stopwatches on the game's and MoonFramework's main systems, read through /perf. They cost frame time: reload " +
                    "or disable the module to take them out", r => Timers());
            Api.Post("/perf/time", "?type=Full.Name&method=Name/paramCount : a stopwatch on one method, read through /perf", r => TimeOne(r));
            Api.Get("/perf/methods", "?type=X : the methods a class declares, to pick what to time", r => Perf.Methods(r.Query["type"] ?? ""));
            Api.Get("/census", "?top= : every component class counted, with which have per-frame methods", r => Census.Json(Api.Qi(r, "top", 40)));
            Api.Get("/perf/fixes",
                "The fps fixes: power (fast link on, offer back-off frames) and audio (silent beyond m metres), with their counters", r => Fixes());
            Api.Post("/perf/fixes/modify",
                "?name=power&on=1|0 / ?name=power&backoffOn=1|0&backoff=N / ?name=audio&on=1|0&m=N : switch a fix on or off, or set its value (saved in BepInEx/config/LunarLink.Perf.cfg)",
                r => Modify(r));
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
            PerfSettings.Update(UnityEngine.Time.unscaledTime);
            Perf.Frame();
            if (!PowerFix.Installed && PowerFix.On) PowerFix.Install();
            if (PowerFix.OfferBackoff > 1) PowerFix.InstallOffer();
            if (!AudioFix.Installed && AudioFix.FarMetres > 0f) AudioFix.Install();
        }

        static string Read()
        {
            var o = Perf.Json();
            return EngineTimers.Running ? o.Raw("engineMs", EngineTimers.Json()) : o;
        }

        static string Engine(bool on)
        {
            if (on) { try { EngineTimers.Start(); } catch (Exception e) { return Api.Result(false, e.Message); } }
            else EngineTimers.Stop();
            return new JsonOut().Obj().Bool("ok", true).Bool("engine", EngineTimers.Running);
        }

        static string Timers()
        {
            string Installed() => new JsonOut().Obj().Bool("ok", true).Str("installed", Perf.Installed);
            if (Perf.Main) return Installed();
            Pump.Journal("perf: installing timers on request");
            try { Perf.Install(new Harmony(Id + ".timers")); }
            catch (Exception e) { Pump.Journal("perf: timers failed: " + e.Message); return Api.Result(false, e.Message); }
            Pump.Journal("perf: timers installed: " + Perf.Installed);
            return Installed();
        }

        static string TimeOne(Req r)
        {
            try { return Perf.TimeOne(new Harmony(Id + ".time"), r.Query["type"] ?? "", r.Query["method"] ?? "Update"); }
            catch (Exception e) { return Api.Result(false, e.Message); }
        }

        static string Fixes() => new JsonOut().Obj().Raw("power", PowerFix.Json()).Raw("audio", AudioFix.Json());

        static string Modify(Req r)
        {
            switch (r.Query["name"])
            {
                case "power":
                    if (r.Query["backoff"] != null) PerfSettings.SetBackoff(Api.Qi(r, "backoff", 1));
                    if (r.Query["backoffOn"] != null) PerfSettings.SetBackoffOn(r.Query["backoffOn"] == "1");
                    if (r.Query["on"] != null) PerfSettings.SetPower(r.Query["on"] == "1");
                    return new JsonOut().Obj().Bool("ok", true).Raw("power", PowerFix.Json());
                case "audio":
                    if (r.Query["m"] != null) PerfSettings.SetSilence(Api.Qf(r, "m", 60f));
                    if (r.Query["on"] != null) PerfSettings.SetSilenceOn(r.Query["on"] == "1");
                    return new JsonOut().Obj().Bool("ok", true).Raw("audio", AudioFix.Json());
            }
            return Api.Result(false, "name=power or name=audio");
        }
    }
}
