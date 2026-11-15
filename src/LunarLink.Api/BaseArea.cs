using System;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    // Is a base working: production machines, capsule catchers, the solar grids and the robots.
    static class BaseArea
    {
        internal static void Start()
        {
            Api.Get("/production", "Every production machine: cycles, progress, power, status, stored dust/sand/stone per slot", r => Production(Api.World));
            Api.Get("/catchers", "Every capsule catcher: kg stored, full, and whether its outlet works", r => Catchers(Api.World));
            Api.Get("/power", "The solar grids: supply, demand, use, satisfaction, battery charge; and the panel types", r => Power(Api.World));
            Api.Get("/base/health",
                "?x=&y=&z=&r=100 : is a base working - machines the game says are stuck (and why), catchers with a dead " +
                    "outlet, robots docked here not cleaning, and power grids short anywhere; idle machines are only counted",
                    r => Health(Api.World, r));
            Api.Get("/robots", "Every J.O.O.N.A.S.: the game's robot map (dock, state, kg carried) with the region under it", r => RobotMap.Robots(Api.World));
        }


        // Everything within r metres of a point that isn't working, in one answer. A machine counts as a problem only when the
        // game gives a reason (status, flow or power text); one simply between cycles or waiting says nothing and is only
        // counted (idle). A full catcher is normal while its outlet drains it, so only a dead outlet is a problem. Grids
        // have no position, so every short grid is listed.
        static string Health(MoonWorld w, Req req)
        {
            var at = new Vector3(Api.Qf(req, "x", 0), Api.Qf(req, "y", 0), Api.Qf(req, "z", 0)); float r = Api.Qf(req, "r", 100f);
            bool Near(int id) => w.depots.TryGetValue(id, out var d) && (new Vector3(d.x, d.y, d.z) - at).sqrMagnitude <= r * r;
            int problems = 0, machinesHere = 0, catchersHere = 0, robotsHere = 0, idle = 0;
            var o = new JsonOut().Obj().Arr("machines");
            foreach (var kv in w.production)
            {
                var p = kv.Value; if (!Near(p.Id)) continue; machinesHere++;
                if (p.Running) continue;
                if (string.IsNullOrEmpty(p.Status) && string.IsNullOrEmpty(p.flowStatus) && string.IsNullOrEmpty(p.powerStatus)) { idle++; continue; }
                problems++;
                o.Obj().Num("id", p.Id).Num("kind", p.Kind).Str("status", p.Status).Str("flow", p.flowStatus).Str("power", p.powerStatus).End();
            }
            o.End().Arr("catchers");
            foreach (var kv in w.depots)
            {
                var d = kv.Value; if (d.kind != MoonWorld.CapsuleCatcherKind || !Near(d.id)) continue; catchersHere++;
                bool outlet = w.CatcherHasOutfeed(d.id);
                if (outlet) continue;
                problems++;
                o.Obj().Num("id", d.id).Num("kg", w.CatcherKilograms(d.id)).Bool("full", w.CatcherFull(d)).Bool("outlet", outlet).End();
            }
            o.End().Arr("robots");
            try
            {
                var list = new Il2CppSystem.Collections.Generic.List<MoonWorld.MapRobot>(); w.MapRobots(list);
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list.get_Item(i); if (!Near(m.dock)) continue; robotsHere++;
                    if ((m.status ?? "").StartsWith("Cleaning", StringComparison.OrdinalIgnoreCase)) continue;
                    problems++;
                    o.Obj().Num("dock", m.dock).Num("number", m.number).Str("status", m.status).Num("kg", m.kilograms).End();
                }
            }
            catch (Exception e) { o.Str("robot map failed: " + e.Message); }
            o.End().Arr("shortGrids");
            foreach (var kv in w.powerGrids)
            {
                var g = kv.Value; if (g.machines == 0 || g.satisfaction >= 0.99f) continue;
                problems++;
                o.Obj().Num("id", g.id).Num("machines", g.machines).Num("satisfaction", g.satisfaction)
                 .Num("supplyKw", g.supplyKw).Num("demandKw", g.demandKw).Num("storedKwh", g.storedKwh).End();
            }
            o.End().Obj("here").Num("machines", machinesHere).Num("idleMachines", idle).Num("catchers", catchersHere).Num("robots", robotsHere).End();
            return o.Num("problems", problems).Bool("ok", problems == 0).ToString();
        }

        static string Production(MoonWorld w)
        {
            var o = new JsonOut().Obj().Num("clock", w.productionClock).Arr("machines");
            foreach (var kv in w.production)
            {
                var p = kv.Value;
                o.Obj().Num("id", p.Id).Num("kind", p.Kind).Num("cycles", p.Cycles).Num("progress", p.Progress).Bool("running", p.Running)
                 .Num("power", p.Power).Num("demandKw", p.DemandKw).Num("usedKw", p.UsedKw).Num("lastCycle", p.lastCycle).Num("lastInput", p.lastInput)
                 .Str("status", p.Status).Str("flow", p.flowStatus).Str("powerStatus", p.powerStatus);
                var spec = MoonWorld.Processor(p.Kind);
                if (spec != null && spec.slotNames != null)
                {
                    o.Arr("slots");
                    for (int s = 0; s < spec.slotNames.Length; s++)
                        o.Obj().Str("name", spec.slotNames[s]).Num("stored", p.Stored(s)).Num("capacity", p.Capacity(s)).End();
                    o.End();
                }
                o.End();
            }
            return o.ToString();
        }

        static string Catchers(MoonWorld w)
        {
            var o = new JsonOut().Obj().Num("storeKg", MoonWorld.CatcherStoreKg).Arr("catchers");
            foreach (var kv in w.depots)
            {
                var d = kv.Value;
                if (d.kind != MoonWorld.CapsuleCatcherKind) continue;
                o.Obj().Num("id", d.id).Num("kg", w.CatcherKilograms(d.id)).Bool("full", w.CatcherFull(d)).Bool("outfeed", w.CatcherHasOutfeed(d.id)).End();
            }
            return o.ToString();
        }

        static string Power(MoonWorld w)
        {
            var o = new JsonOut().Obj().Obj("panels");
            foreach (var (name, p) in new[] { ("post", MoonWorld.PostPanel), ("twin", MoonWorld.TwinPanel), ("tracker", MoonWorld.TrackerPanel) })
                if (p == null) o.Null(name);
                else o.Obj(name).Str("name", p.name).Num("peakKw", p.peakKw).Num("batteryKwh", p.batteryKwh).Num("range", p.range).End();
            o.End().Arr("grids");
            foreach (var kv in w.powerGrids)
            {
                var g = kv.Value;
                o.Obj().Num("id", g.id).Num("panels", g.panels).Num("machines", g.machines).Num("supplyKw", g.supplyKw).Num("demandKw", g.demandKw)
                 .Num("usedKw", g.usedKw).Num("peakKw", g.peakKw).Num("satisfaction", g.satisfaction).Num("storedKwh",
                     g.storedKwh).Num("capacityKwh", g.capacityKwh).End();
            }
            return o.ToString();
        }
    }
}
