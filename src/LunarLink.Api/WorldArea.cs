using System;
using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    // What's in the world, read only: structures, belts, stations, terrain, capsules, kinds, the conveyor kit, recipes,
    // the sun, the moon's cleaned share and where dust is left.
    static class WorldArea
    {
        internal static void Start(string state)
        {
            Api.Get("/structures", "Every structure: id, kind, pos, rotation, support. ?x=&y=&z=&r= within r m of a point; ?kind=1,2; ?since=<id> only newer", r => Structures(Api.World, r));
            Api.Get("/belts", "Every belt piece: id, start/end points and directions, length", r => Belts(Api.World));
            Api.Get("/stations", "The stations: index, name, pos, intake sockets", r => Stations(Api.World));
            Api.Get("/stations/shape", "?index= : every collider of a station as the 8 corners of its box", r => StationShape(Api.World, r));
            Api.Get("/stations/footprint", "?index=&step= : where a station's building really is (rays down onto its colliders)", r => StationFootprint(Api.World, r));
            Api.Get("/terrain", "?x=&y=&z=&half=&step= : ground heights on a grid round a point", r => Terrain(Api.World, r));
            Api.Get("/canisters", "?x=&y=&z=&radius= : capsules near a point: cargo, kg, riding, carried; and the totals", r => Canisters(Api.World, r));
            Api.Get("/kinds", "Structure kinds standing in the world; ?all=1 every kind the game defines: name, prefab, price, footprint, height, ports", r => Kinds(Api.World, r.Query["all"] == "1"));
            Api.Get("/kit", "The conveyor kit's limits: length, radius, slope, height, price per metre", r => Kit());
            Api.Get("/specs", "Machine recipes: batch sizes, cycle times, slots, belt speed", r => Specs());
            Api.Get("/sun", "The sun: direction, height, lunar day length, clock", r => Sun());
            Api.Get("/moon", "The cleaned share of the moon, as the map screen shows it", r => MoonStat());
            Api.Get("/dust/regions", "Every region not complete: index, centre, dust left, dust it started with", r => DustMap.Regions(Api.World));
            Api.Get("/dust/geometry", "Region centres and chunk-file centres, written to dust_geometry.json for offline save editing", r => DustMap.Geometry(Api.World));
        }


        // One entry per structure kind actually built: display name, footprint and conveyor ports.
        // the build kinds standing in the world, or (all=1) every kind the game defines (1-1024) - with its prefab name,
        // the name blueprints use (MoonPrints "prefab:stone_press", "asset:square_foundation_0_25m")
        static string Kinds(MoonWorld w, bool all)
        {
            var seen = new System.Collections.Generic.HashSet<int>();
            if (all) { for (int k = 1; k <= 1024; k++) { try { if (w.BuildDefinition(k) != null) seen.Add(k); } catch { } } }
            else foreach (var kv in w.depots) seen.Add(kv.Value.kind);
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var k in seen)
            {
                var d = w.BuildDefinition(k);
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"kind\":").Append(k);
                if (d == null) { sb.Append(",\"name\":null}"); continue; }
                string prefab = null; try { prefab = d.prefab != null ? d.prefab.name : null; } catch { }
                sb.Append(",\"name\":").Append(J.S(d.displayName)).Append(",\"prefab\":").Append(J.S(prefab))
                  .Append(",\"price\":").Append(d.priceDollars)
                  .Append(",\"scale\":").Append(J.F(d.scale))
                  .Append(",\"halfFootprint\":[").Append(J.F(d.halfFootprint.x)).Append(',').Append(J.F(d.halfFootprint.y)).Append(']')
                  .Append(",\"height\":").Append(J.F(d.height))
                  .Append(",\"floorOffset\":").Append(J.F(d.floorOffset))
                  .Append(",\"reach\":").Append(J.F(d.reach)).Append(",\"groundTolerance\":").Append(J.F(d.groundTolerance))
                  .Append(",\"ports\":[");
                var ports = d.conveyorPorts;
                for (int i = 0; ports != null && i < ports.Length; i++)
                {
                    var p = ports[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(J.S(p.name)).Append(",\"pos\":").Append(J.V(p.position))
                      .Append(",\"dir\":").Append(J.V(p.direction)).Append(",\"output\":").Append(p.output ? "true" : "false").Append('}');
                }
                sb.Append("]}");
            }
            return sb.Append(']').ToString();
        }

        // ?x=&y=&z=&r= : only within r metres of that world point; ?kind=1,2 : only those kinds; ?since=<id> : only pieces
        // with a higher id (what was just built) - a short answer instead of thousands
        static string Structures(MoonWorld w, Req req)
        {
            int since = Api.Qi(req, "since", -1); float r = Api.Qf(req, "r", -1f);
            var at = new Vector3(Api.Qf(req, "x", 0), Api.Qf(req, "y", 0), Api.Qf(req, "z", 0));
            var kinds = new System.Collections.Generic.HashSet<int>(Api.Ids(req, "kind"));
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var kv in w.depots)
            {
                var d = kv.Value;
                if (since >= 0 && d.id <= since) continue;
                if (kinds.Count > 0 && !kinds.Contains(d.kind)) continue;
                if (r >= 0 && (new Vector3(d.x, d.y, d.z) - at).sqrMagnitude > r * r) continue;
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"id\":").Append(d.id).Append(",\"kind\":").Append(d.kind)
                  .Append(",\"pos\":[").Append(J.F(d.x)).Append(',').Append(J.F(d.y)).Append(',').Append(J.F(d.z)).Append(']')
                  .Append(",\"yaw\":").Append(J.F(d.yaw)).Append(",\"placedPose\":").Append(d.placedPose ? "true" : "false")
                  .Append(",\"q\":[").Append(J.F(d.qx)).Append(',').Append(J.F(d.qy)).Append(',').Append(J.F(d.qz)).Append(',').Append(J.F(d.qw)).Append(']')
                  .Append(",\"support\":").Append(d.support).Append(",\"stored\":").Append(J.F(d.stored)).Append('}');
            }
            return sb.Append(']').ToString();
        }

        static string Belts(MoonWorld w)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var kv in w.conveyors)
            {
                var b = kv.Value;
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"id\":").Append(b.id).Append(",\"start\":").Append(J.V(b.start)).Append(",\"startDir\":").Append(J.V(b.startDirection))
                  .Append(",\"end\":").Append(J.V(b.end)).Append(",\"endDir\":").Append(J.V(b.endDirection));
                var path = w.BeltPathOf(b.id);
                if (path != null) sb.Append(",\"length\":").Append(J.F(path.Length));
                sb.Append('}');
            }
            return sb.Append(']').ToString();
        }

        static string Stations(MoonWorld w)
        {
            var sb = new StringBuilder("[");
            int i = 0;
            foreach (var s in w.stations)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"index\":").Append(i).Append(",\"name\":").Append(J.S(s.gameObject.name))
                  .Append(",\"pos\":").Append(J.V(s.transform.position)).Append(",\"sockets\":[");
                var socks = s.intakeSockets;
                for (int k = 0; socks != null && k < socks.Length; k++)
                {
                    var t = socks[k];
                    if (k > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(J.S(t.name)).Append(",\"pos\":").Append(J.V(t.position)).Append(",\"fwd\":").Append(J.V(t.forward)).Append('}');
                }
                sb.Append("]}");
                i++;
            }
            return sb.Append(']').ToString();
        }

        // /stations/shape?index= : every collider of a station building, as the 8 corners of its own box (mesh or box
        // collider bounds in its local frame, turned into the world) - the real footprint for the layout planner, which
        // had only a hand-set box (units clipped the building, 2026-11-04). Read only.
        static string StationShape(MoonWorld w, Req req)
        {
            int want = Api.Qi(req, "index", -1); int i = 0; var sb = new StringBuilder("["); int n = 0;
            foreach (var s in w.stations)
            {
                if (want >= 0 && i != want) { i++; continue; }
                var cols = s.gameObject.GetComponentsInChildren<Collider>(true);
                for (int k = 0; cols != null && k < cols.Length; k++)
                {
                    var c = cols[k]; if (c == null) continue;
                    try
                    {
                        Vector3 lc, ls; bool local = true;
                        var bc = c.TryCast<BoxCollider>(); var mc = c.TryCast<MeshCollider>();
                        if (bc != null) { lc = bc.center; ls = bc.size; }
                        else if (mc != null && mc.sharedMesh != null) { var b = mc.sharedMesh.bounds; lc = b.center; ls = b.size; }
                        else { var b = c.bounds; lc = b.center; ls = b.size; local = false; }
                        if (n++ > 0) sb.Append(",");
                        sb.Append("{\"station\":").Append(i).Append(",\"name\":").Append(J.S(c.gameObject.name))
                          .Append(",\"trigger\":").Append(c.isTrigger ? "true" : "false").Append(",\"enabled\":").Append(c.enabled && c.gameObject.activeInHierarchy ? "true" : "false")
                          .Append(",\"corners\":[");
                        int m = 0;
                        for (int a = -1; a <= 1; a += 2) for (int bb = -1; bb <= 1; bb += 2) for (int cc = -1; cc <= 1; cc += 2)
                        {
                            var q = lc + Vector3.Scale(ls * 0.5f, new Vector3(a, bb, cc));
                            var wq = local ? c.transform.TransformPoint(q) : q;
                            if (m++ > 0) sb.Append(","); sb.Append(J.V(wq));
                        }
                        sb.Append("]}");
                    }
                    catch { }
                }
                i++;
            }
            return sb.Append("]").ToString();
        }

        // /stations/footprint?index=&step=1 : where the station building really is - a ray straight down onto each of
        // its colliders every `step` m across that collider's own box (Collider.Raycast: only that collider, cheap).
        // The mesh's bounding box (/stations/shape) is a rectangle; the building is not (a U-shaped part south of the
        // intakes, 2026-11-04). Returns the hit points (world). Read only.
        static string StationFootprint(MoonWorld w, Req req)
        {
            int want = Api.Qi(req, "index", -1); float step = Mathf.Clamp(Api.Qf(req, "step", 1f), 0.5f, 4f);
            int i = 0; var sb = new StringBuilder("["); int n = 0;
            foreach (var s in w.stations)
            {
                if (want >= 0 && i != want) { i++; continue; }
                var cols = s.gameObject.GetComponentsInChildren<Collider>(false);
                for (int k = 0; cols != null && k < cols.Length && n < 20000; k++)
                {
                    var c = cols[k]; if (c == null || c.isTrigger || !c.enabled) continue;
                    try
                    {
                        Vector3 lc, ls; var bc = c.TryCast<BoxCollider>(); var mc = c.TryCast<MeshCollider>();
                        if (bc != null) { lc = bc.center; ls = bc.size; }
                        else if (mc != null && mc.sharedMesh != null) { var b = mc.sharedMesh.bounds; lc = b.center; ls = b.size; }
                        else continue;
                        var tr = c.transform; var up = s.transform.up;
                        // the collider's box, laid out along the STATION's axes (its walls stand on the station's up)
                        var sx = s.transform.right; var sz = s.transform.forward;
                        var wc = tr.TransformPoint(lc); var half = Vector3.Scale(ls * 0.5f, tr.lossyScale);
                        float r = half.magnitude; float top = r + 2f;
                        for (float x = -r; x <= r; x += step)
                            for (float z = -r; z <= r; z += step)
                            {
                                var o = wc + sx * x + sz * z + up * top;
                                RaycastHit hit;
                                if (c.Raycast(new Ray(o, -up), out hit, top * 2f))
                                {
                                    if (n++ > 0) sb.Append(",");
                                    sb.Append("[").Append(J.F(hit.point.x)).Append(",").Append(J.F(hit.point.y)).Append(",").Append(J.F(hit.point.z)).Append("]");
                                }
                            }
                    }
                    catch { }
                }
                i++;
            }
            return sb.Append("]").ToString();
        }

        // /moon : the cleaned share of the moon as the game shows it (the map screen's moon-percent label) - read only
        static string MoonStat()
        {
            var sb = new StringBuilder("{\"labels\":["); int n = 0;
            var objs = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(typeof(MoonMapScreen)), true);
            for (int i = 0; objs != null && i < objs.Length; i++)
            {
                try { var t = objs[i].Cast<MoonMapScreen>().moonPercent; if (t == null) continue; if (n++ > 0) sb.Append(","); sb.Append(J.S(t.text)); } catch { }
            }
            return sb.Append("]}").ToString();
        }

        // Loose capsules (dust/sand/stone) lying on the ground or riding belts, within radius of x,y,z.
        static string Canisters(MoonWorld w, Req req)
        {
            var c = new Vector3(Api.Qf(req, "x", 0), Api.Qf(req, "y", 0), Api.Qf(req, "z", 0));
            float r = Api.Qf(req, "radius", 200);
            var sb = new StringBuilder("{\"total\":").Append(w.DroppedCanisterCount).Append(",\"riding\":").Append(w.RidingCanisterCount).Append(",\"near\":[");
            bool first = true;
            foreach (var kv in w.canisters)
            {
                var k = kv.Value;
                if ((k.position - c).magnitude > r) continue;
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"id\":").Append(k.id).Append(",\"kg\":").Append(J.F(k.kilograms)).Append(",\"cargo\":").Append(J.S(k.cargo.ToString()))
                  .Append(",\"pos\":").Append(J.V(k.position))
                  .Append(",\"riding\":").Append(w.IsCanisterRiding(k.id) ? "true" : "false")
                  .Append(",\"carried\":").Append(w.IsCanisterCarried(k.id) ? "true" : "false").Append('}');
            }
            return sb.Append("]}").ToString();
        }

        static string Kit()
        {
            var k = MoonConveyorKit.Load();
            if (k == null) return "{\"error\":\"no conveyor kit\"}";
            return "{\"minLength\":" + J.F(k.minLength) + ",\"maxLength\":" + J.F(k.maxLength) + ",\"minRadius\":" + J.F(k.minRadius)
                + ",\"maxSlope\":" + J.F(k.maxSlope) + ",\"minHeight\":" + J.F(k.minHeight) + ",\"maxHeight\":" + J.F(k.maxHeight)
                + ",\"deckHeight\":" + J.F(k.deckHeight) + ",\"pricePerMetre\":" + J.F(k.pricePerMetre) + ",\"builder\":" + (Api.Player != null ? "true" : "false") + "}";
        }





        // Day/night clock: lunar day length, clock speed and the current sun direction.
        static string Sun()
        {
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonSolarSystem>());
            if (found.Length == 0) return "{\"error\":\"no solar system\"}";
            var s = found[0].Cast<MoonSolarSystem>();
            var dir = s.sunlight != null ? -s.sunlight.transform.forward : Vector3.zero;
            return "{\"time\":" + J.F(Time.time) + ",\"lunarDaySeconds\":" + J.F(s.lunarDaySeconds)
                + ",\"clockSpeed\":" + J.F(MoonSolarSystem.clockSpeed) + ",\"clockOffset\":" + J.F(MoonSolarSystem.clockOffset)
                + ",\"spinAxis\":" + J.V(MoonSolarSystem.SpinAxis) + ",\"skySun\":" + J.V(s.skySun)
                + ",\"sunlightDir\":" + J.V(dir) + ",\"sunHeight\":" + J.F(s.SunHeight) + "}";
        }

        // Recipe definitions for the processing machines.
        static string Specs()
        {
            var sb = new StringBuilder("{\"chamberBatchKg\":").Append(J.F(MoonWorld.ChamberBatchKg))
                .Append(",\"pressDustPerBlockKg\":").Append(J.F(MoonWorld.PressDustPerBlockKg))
                .Append(",\"pressSandPerBlockKg\":").Append(J.F(MoonWorld.PressSandPerBlockKg))
                .Append(",\"stoneBlockKg\":").Append(J.F(MoonWorld.StoneBlockKg))
                .Append(",\"chamberPartBatchSeconds\":").Append(J.F(MoonWorld.ChamberPartBatchSeconds))
                .Append(",\"beltSpeed\":").Append(J.F(MoonWorld.BeltSpeed)).Append(",\"beltSpacing\":").Append(J.F(MoonWorld.BeltSpacing))
                .Append(",\"machines\":[");
            int[] kinds = { 96, 97 };
            for (int i = 0; i < kinds.Length; i++)
            {
                var s = MoonWorld.Processor(kinds[i]);
                if (i > 0) sb.Append(',');
                if (s == null) { sb.Append("null"); continue; }
                sb.Append("{\"kind\":").Append(kinds[i]).Append(",\"name\":").Append(J.S(s.name)).Append(",\"product\":").Append(J.S(s.product))
                  .Append(",\"runningKw\":").Append(J.F(s.runningKw)).Append(",\"cycleSeconds\":").Append(J.F(s.cycleSeconds))
                  .Append(",\"output\":").Append(s.output).Append(",\"slots\":[");
                for (int k = 0; s.slotNames != null && k < s.slotNames.Length; k++)
                {
                    if (k > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(J.S(s.slotNames[k])).Append(",\"cargo\":").Append(J.S(s.slotCargo[k].ToString()))
                      .Append(",\"capacity\":").Append(J.F(s.slotCapacity[k])).Append('}');
                }
                sb.Append("]}");
            }
            return sb.Append("]}").ToString();
        }

        // Real ground heights on a square grid around a world point, in the same east/north/up frame
        // the save tools use (east = cross(Y, up)).
        static string Terrain(MoonWorld w, Req req)
        {
            float Q(string k, float dflt) => float.TryParse(req.Query[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : dflt;
            var c = new Vector3(Q("x", 0), Q("y", 2000), Q("z", 0));
            float half = Mathf.Clamp(Q("half", 100), 5, 400), step = Mathf.Clamp(Q("step", 2), 0.5f, 20);
            var up = c.normalized;
            var east = Vector3.Cross(Vector3.up, up);
            if (east.sqrMagnitude < 1e-6f) east = Vector3.Cross(Vector3.right, up);
            east.Normalize();
            var north = Vector3.Cross(up, east);
            int n = (int)(2 * half / step) + 1;
            var sb = new StringBuilder("{\"centre\":").Append(J.V(c)).Append(",\"half\":").Append(J.F(half))
                .Append(",\"step\":").Append(J.F(step)).Append(",\"n\":").Append(n).Append(",\"h\":[");
            var terrain = w.Terrain;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    var p = c + east * (-half + i * step) + north * (half - j * step);
                    var s = terrain.SurfacePoint(p.normalized);
                    if (i > 0 || j > 0) sb.Append(',');
                    sb.Append(J.F(Vector3.Dot(s - c, up)));
                }
            return sb.Append("]}").ToString();
        }
    }
}
