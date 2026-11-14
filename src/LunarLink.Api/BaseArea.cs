using System;
using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    // Is a base working: production machines, capsule catchers, the solar grids and the robots.
    static class BaseArea
    {
        internal static void Start(string state)
        {
            Api.Get("/production", "Every production machine: cycles, progress, power, status, stored dust/sand/stone per slot", r => Production(Api.World));
            Api.Get("/catchers", "Every capsule catcher: kg stored, full, and whether its outlet works", r => Catchers(Api.World));
            Api.Get("/power", "The solar grids: supply, demand, use, satisfaction, battery charge; and the panel types", r => Power(Api.World));
            Api.Get("/base/health", "?x=&y=&z=&r=100 : is a base working - machines the game says are stuck (and why), catchers with a dead outlet, robots docked here not cleaning, and power grids short anywhere; idle machines are only counted", r => Health(Api.World, r));
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
            var sb = new StringBuilder("{\"machines\":[");
            bool first = true;
            foreach (var kv in w.production)
            {
                var p = kv.Value; if (!Near(p.Id)) continue; machinesHere++;
                if (p.Running) continue;
                if (string.IsNullOrEmpty(p.Status) && string.IsNullOrEmpty(p.flowStatus) && string.IsNullOrEmpty(p.powerStatus)) { idle++; continue; }
                if (!first) sb.Append(','); first = false; problems++;
                sb.Append("{\"id\":").Append(p.Id).Append(",\"kind\":").Append(p.Kind).Append(",\"status\":").Append(J.S(p.Status))
                  .Append(",\"flow\":").Append(J.S(p.flowStatus)).Append(",\"power\":").Append(J.S(p.powerStatus)).Append('}');
            }
            sb.Append("],\"catchers\":["); first = true;
            foreach (var kv in w.depots)
            {
                var d = kv.Value; if (d.kind != MoonWorld.CapsuleCatcherKind || !Near(d.id)) continue; catchersHere++;
                bool full = w.CatcherFull(d), outlet = w.CatcherHasOutfeed(d.id);
                if (outlet) continue;
                if (!first) sb.Append(','); first = false; problems++;
                sb.Append("{\"id\":").Append(d.id).Append(",\"kg\":").Append(J.F(w.CatcherKilograms(d.id))).Append(",\"full\":").Append(full ? "true" : "false")
                  .Append(",\"outlet\":").Append(outlet ? "true" : "false").Append('}');
            }
            sb.Append("],\"robots\":["); first = true;
            try
            {
                var list = new Il2CppSystem.Collections.Generic.List<MoonWorld.MapRobot>(); w.MapRobots(list);
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list.get_Item(i); if (!Near(m.dock)) continue; robotsHere++;
                    if ((m.status ?? "").StartsWith("Cleaning", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!first) sb.Append(','); first = false; problems++;
                    sb.Append("{\"dock\":").Append(m.dock).Append(",\"number\":").Append(m.number).Append(",\"status\":").Append(J.S(m.status))
                      .Append(",\"kg\":").Append(J.F(m.kilograms)).Append('}');
                }
            }
            catch (Exception e) { sb.Append(J.S("robot map failed: " + e.Message)); }
            sb.Append("],\"shortGrids\":["); first = true;
            foreach (var kv in w.powerGrids)
            {
                var g = kv.Value; if (g.machines == 0 || g.satisfaction >= 0.99f) continue;
                if (!first) sb.Append(','); first = false; problems++;
                sb.Append("{\"id\":").Append(g.id).Append(",\"machines\":").Append(g.machines).Append(",\"satisfaction\":").Append(J.F(g.satisfaction))
                  .Append(",\"supplyKw\":").Append(J.F(g.supplyKw)).Append(",\"demandKw\":").Append(J.F(g.demandKw)).Append(",\"storedKwh\":").Append(J.F(g.storedKwh)).Append('}');
            }
            return sb.Append("],\"here\":{\"machines\":").Append(machinesHere).Append(",\"idleMachines\":").Append(idle).Append(",\"catchers\":").Append(catchersHere).Append(",\"robots\":").Append(robotsHere)
                .Append("},\"problems\":").Append(problems).Append(",\"ok\":").Append(problems == 0 ? "true" : "false").Append('}').ToString();
        }

        static string Production(MoonWorld w)
        {
            var sb = new StringBuilder("{\"clock\":").Append(J.F(w.productionClock)).Append(",\"machines\":[");
            bool first = true;
            foreach (var kv in w.production)
            {
                var p = kv.Value;
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"id\":").Append(p.Id).Append(",\"kind\":").Append(p.Kind)
                  .Append(",\"cycles\":").Append(p.Cycles).Append(",\"progress\":").Append(J.F(p.Progress))
                  .Append(",\"running\":").Append(p.Running ? "true" : "false")
                  .Append(",\"power\":").Append(J.F(p.Power)).Append(",\"demandKw\":").Append(J.F(p.DemandKw)).Append(",\"usedKw\":").Append(J.F(p.UsedKw))
                  .Append(",\"lastCycle\":").Append(J.F(p.lastCycle)).Append(",\"lastInput\":").Append(J.F(p.lastInput))
                  .Append(",\"status\":").Append(J.S(p.Status)).Append(",\"flow\":").Append(J.S(p.flowStatus)).Append(",\"powerStatus\":").Append(J.S(p.powerStatus));
                var spec = MoonWorld.Processor(p.Kind);
                if (spec != null && spec.slotNames != null)
                {
                    sb.Append(",\"slots\":[");
                    for (int s = 0; s < spec.slotNames.Length; s++)
                    {
                        if (s > 0) sb.Append(',');
                        sb.Append("{\"name\":").Append(J.S(spec.slotNames[s]))
                          .Append(",\"stored\":").Append(J.F(p.Stored(s))).Append(",\"capacity\":").Append(J.F(p.Capacity(s))).Append('}');
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }
            return sb.Append("]}").ToString();
        }

        static string Catchers(MoonWorld w)
        {
            var sb = new StringBuilder("{\"storeKg\":").Append(J.F(MoonWorld.CatcherStoreKg)).Append(",\"catchers\":[");
            bool first = true;
            foreach (var kv in w.depots)
            {
                var d = kv.Value;
                if (d.kind != MoonWorld.CapsuleCatcherKind) continue;
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"id\":").Append(d.id).Append(",\"kg\":").Append(J.F(w.CatcherKilograms(d.id)))
                  .Append(",\"full\":").Append(w.CatcherFull(d) ? "true" : "false")
                  .Append(",\"outfeed\":").Append(w.CatcherHasOutfeed(d.id) ? "true" : "false").Append('}');
            }
            return sb.Append("]}").ToString();
        }

        static string Power(MoonWorld w)
        {
            string Panel(MoonWorld.PanelSpec p) => p == null ? "null" :
                "{\"name\":" + J.S(p.name) + ",\"peakKw\":" + J.F(p.peakKw) + ",\"batteryKwh\":" + J.F(p.batteryKwh) + ",\"range\":" + J.F(p.range) + "}";
            var sb = new StringBuilder("{\"panels\":{\"post\":").Append(Panel(MoonWorld.PostPanel))
                .Append(",\"twin\":").Append(Panel(MoonWorld.TwinPanel)).Append(",\"tracker\":").Append(Panel(MoonWorld.TrackerPanel))
                .Append("},\"grids\":[");
            bool first = true;
            foreach (var kv in w.powerGrids)
            {
                var g = kv.Value;
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"id\":").Append(g.id).Append(",\"panels\":").Append(g.panels)
                  .Append(",\"machines\":").Append(g.machines)
                  .Append(",\"supplyKw\":").Append(J.F(g.supplyKw)).Append(",\"demandKw\":").Append(J.F(g.demandKw))
                  .Append(",\"usedKw\":").Append(J.F(g.usedKw)).Append(",\"peakKw\":").Append(J.F(g.peakKw))
                  .Append(",\"satisfaction\":").Append(J.F(g.satisfaction))
                  .Append(",\"storedKwh\":").Append(J.F(g.storedKwh)).Append(",\"capacityKwh\":").Append(J.F(g.capacityKwh)).Append('}');
            }
            return sb.Append("]}").ToString();
        }

    }
}
