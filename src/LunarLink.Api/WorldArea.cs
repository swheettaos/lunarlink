using System.Globalization;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    // What's in the world, read only: structures, belts, stations, terrain, capsules, kinds, the conveyor kit, recipes,
    // the sun, the moon's cleaned share and where dust is left.
    static class WorldArea
    {
        internal static void Start()
        {
            Api.Get("/structures",
                "Every structure: id, kind, pos, rotation, support. ?x=&y=&z=&r= within r m of a point; ?kind=1,2; ?since=<id> " +
                    "only newer", r => Structures(Api.World, r));
            Api.Get("/belts", "Every belt piece: id, start/end points and directions, length", r => Belts(Api.World));
            Api.Get("/stations", "The stations: index, name, pos, intake sockets", r => Stations(Api.World));
            Api.Get("/stations/shape", "?index= : every collider of a station as the 8 corners of its box", r => StationShape(Api.World, r));
            Api.Get("/stations/footprint", "?index=&step= : where a station's building really is (rays down onto its colliders)",
                r => StationFootprint(Api.World, r));
            Api.Get("/terrain", "?x=&y=&z=&half=&step= : ground heights on a grid round a point", r => Terrain(Api.World, r));
            Api.Get("/canisters", "?x=&y=&z=&radius= : capsules near a point: cargo, kg, riding, carried; and the totals", r => Canisters(Api.World, r));
            Api.Get("/kinds", "Structure kinds standing in the world; ?all=1 every kind the game defines: name, prefab, price, " +
                "footprint, height, ports", r => Kinds(Api.World, r.Query["all"] == "1"));
            Api.Get("/kit", "The conveyor kit's limits: length, radius, slope, height, price per metre", r => Kit());
            Api.Get("/specs", "Machine recipes: batch sizes, cycle times, slots, belt speed", r => Specs());
            Api.Get("/sun", "The sun: direction, height, lunar day length, clock", r => Sun());
            Api.Get("/moon", "The cleaned share of the moon, as the map screen shows it", r => MoonStat());
            Api.Get("/dust/regions", "Every region not complete: index, centre, dust left, dust it started with", r => DustMap.Regions(Api.World));
            Api.Get("/dust/geometry",
                "Region centres and chunk-file centres, written to dust_geometry.json for offline save editing",
                r => DustMap.Geometry(Api.World));
        }


        // One entry per structure kind actually built: display name, footprint and conveyor ports.
        // the build kinds standing in the world, or (all=1) every kind the game defines (1-1024) - with its prefab name,
        // the name blueprints use (MoonPrints "prefab:stone_press", "asset:square_foundation_0_25m")
        static string Kinds(MoonWorld w, bool all)
        {
            var seen = new System.Collections.Generic.HashSet<int>();
            if (all) { for (int k = 1; k <= 1024; k++) { try { if (w.BuildDefinition(k) != null) seen.Add(k); }
                catch (System.Exception caught) { Quiet.Note(caught); } } }
            else foreach (var kv in w.depots) seen.Add(kv.Value.kind);
            var o = new JsonOut().Arr();
            foreach (var k in seen)
            {
                var d = w.BuildDefinition(k);
                o.Obj().Num("kind", k);
                if (d == null) { o.Null("name").End(); continue; }
                string prefab = null; try { prefab = d.prefab != null ? d.prefab.name : null; } catch (System.Exception caught) { Quiet.Note(caught); }
                o.Str("name", d.displayName).Str("prefab", prefab).Num("price", d.priceDollars).Num("scale", d.scale)
                 .Nums("halfFootprint", d.halfFootprint.x, d.halfFootprint.y).Num("height", d.height).Num("floorOffset", d.floorOffset)
                 .Num("reach", d.reach).Num("groundTolerance", d.groundTolerance).Arr("ports");
                var ports = d.conveyorPorts;
                for (int i = 0; ports != null && i < ports.Length; i++)
                    o.Obj().Str("name", ports[i].name).Vec("pos", ports[i].position).Vec("dir", ports[i].direction).Bool("output", ports[i].output).End();
                o.End().End();
            }
            return o.ToString();
        }

        // ?x=&y=&z=&r= : only within r metres of that world point; ?kind=1,2 : only those kinds; ?since=<id> : only pieces
        // with a higher id (what was just built) - a short answer instead of thousands
        static string Structures(MoonWorld w, Req req)
        {
            int since = Api.Qi(req, "since", -1); float r = Api.Qf(req, "r", -1f);
            var at = new Vector3(Api.Qf(req, "x", 0), Api.Qf(req, "y", 0), Api.Qf(req, "z", 0));
            var kinds = new System.Collections.Generic.HashSet<int>(Api.Ids(req, "kind"));
            var o = new JsonOut().Arr();
            foreach (var kv in w.depots)
            {
                var d = kv.Value;
                if (since >= 0 && d.id <= since) continue;
                if (kinds.Count > 0 && !kinds.Contains(d.kind)) continue;
                if (r >= 0 && (new Vector3(d.x, d.y, d.z) - at).sqrMagnitude > r * r) continue;
                o.Obj().Num("id", d.id).Num("kind", d.kind).Nums("pos", d.x, d.y, d.z).Num("yaw", d.yaw).Bool("placedPose", d.placedPose)
                 .Nums("q", d.qx, d.qy, d.qz, d.qw).Num("support", d.support).Num("stored", d.stored).End();
            }
            return o.ToString();
        }

        static string Belts(MoonWorld w)
        {
            var o = new JsonOut().Arr();
            foreach (var kv in w.conveyors)
            {
                var b = kv.Value;
                o.Obj().Num("id", b.id).Vec("start", b.start).Vec("startDir", b.startDirection).Vec("end", b.end).Vec("endDir", b.endDirection);
                var path = w.BeltPathOf(b.id);
                if (path != null) o.Num("length", path.Length);
                o.End();
            }
            return o.ToString();
        }

        static string Stations(MoonWorld w)
        {
            var o = new JsonOut().Arr();
            int i = 0;
            foreach (var s in w.stations)
            {
                o.Obj().Num("index", i++).Str("name", s.gameObject.name).Vec("pos", s.transform.position).Arr("sockets");
                var socks = s.intakeSockets;
                for (int k = 0; socks != null && k < socks.Length; k++)
                    o.Obj().Str("name", socks[k].name).Vec("pos", socks[k].position).Vec("fwd", socks[k].forward).End();
                o.End().End();
            }
            return o.ToString();
        }

        // /stations/shape?index= : every collider of a station building, as the 8 corners of its own box (mesh or box
        // collider bounds in its local frame, turned into the world) - the real footprint for the layout planner, which
        // had only a hand-set box (units clipped the building, 2026-11-04). Read only.
        static string StationShape(MoonWorld w, Req req)
        {
            int want = Api.Qi(req, "index", -1); int i = 0;
            var o = new JsonOut().Arr();
            foreach (var s in w.stations)
            {
                if (want >= 0 && i != want) { i++; continue; }
                var cols = s.gameObject.GetComponentsInChildren<Collider>(true);
                for (int k = 0; cols != null && k < cols.Length; k++)
                {
                    var c = cols[k]; if (c == null) continue;
                    var corners = new System.Collections.Generic.List<Vector3>();
                    try
                    {
                        Vector3 lc, ls; bool local = true;
                        var bc = c.TryCast<BoxCollider>(); var mc = c.TryCast<MeshCollider>();
                        if (bc != null) { lc = bc.center; ls = bc.size; }
                        else if (mc != null && mc.sharedMesh != null) { var b = mc.sharedMesh.bounds; lc = b.center; ls = b.size; }
                        else { var b = c.bounds; lc = b.center; ls = b.size; local = false; }
                        for (int a = -1; a <= 1; a += 2) for (int bb = -1; bb <= 1; bb += 2) for (int cc = -1; cc <= 1; cc += 2)
                        {
                            var q = lc + Vector3.Scale(ls * 0.5f, new Vector3(a, bb, cc));
                            corners.Add(local ? c.transform.TransformPoint(q) : q);
                        }
                    }
                    catch (System.Exception caught) { Quiet.Note(caught); continue; }      // read first, written whole: a failure leaves no half entry
                    o.Obj().Num("station", i).Str("name", c.gameObject.name).Bool("trigger", c.isTrigger).Bool("enabled",
                        c.enabled && c.gameObject.activeInHierarchy).Arr("corners");
                    foreach (var q in corners) o.Vec(q);
                    o.End().End();
                }
                i++;
            }
            return o.ToString();
        }

        // /stations/footprint?index=&step=1 : where the station building really is - a ray straight down onto each of
        // its colliders every `step` m across that collider's own box (Collider.Raycast: only that collider, cheap).
        // The mesh's bounding box (/stations/shape) is a rectangle; the building is not (a U-shaped part south of the
        // intakes, 2026-11-04). Returns the hit points (world). Read only.
        static string StationFootprint(MoonWorld w, Req req)
        {
            int want = Api.Qi(req, "index", -1); float step = Mathf.Clamp(Api.Qf(req, "step", 1f), 0.5f, 4f);
            int i = 0, n = 0; var o = new JsonOut().Arr();
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
                                if (c.Raycast(new Ray(wc + sx * x + sz * z + up * top, -up), out RaycastHit hit, top * 2f)) { n++; o.Vec(hit.point); }
                    }
                    catch (System.Exception caught) { Quiet.Note(caught); }
                }
                i++;
            }
            return o.ToString();
        }

        // /moon : the cleaned share of the moon as the game shows it (the map screen's moon-percent label) - read only
        static string MoonStat()
        {
            var o = new JsonOut().Obj().Arr("labels");
            var objs = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(typeof(MoonMapScreen)), true);
            for (int i = 0; objs != null && i < objs.Length; i++)
            {
                string text = null;
                try { text = objs[i].Cast<MoonMapScreen>().moonPercent?.text; } catch (System.Exception caught) { Quiet.Note(caught); }
                if (text != null) o.Str(text);
            }
            return o.ToString();
        }

        // Loose capsules (dust/sand/stone) lying on the ground or riding belts, within radius of x,y,z.
        static string Canisters(MoonWorld w, Req req)
        {
            var c = new Vector3(Api.Qf(req, "x", 0), Api.Qf(req, "y", 0), Api.Qf(req, "z", 0));
            float r = Api.Qf(req, "radius", 200);
            var o = new JsonOut().Obj().Num("total", w.DroppedCanisterCount).Num("riding", w.RidingCanisterCount).Arr("near");
            foreach (var kv in w.canisters)
            {
                var k = kv.Value;
                if ((k.position - c).magnitude > r) continue;
                o.Obj().Num("id", k.id).Num("kg", k.kilograms).Str("cargo", k.cargo.ToString()).Vec("pos", k.position)
                 .Bool("riding", w.IsCanisterRiding(k.id)).Bool("carried", w.IsCanisterCarried(k.id)).End();
            }
            return o.ToString();
        }

        static string Kit()
        {
            var k = MoonConveyorKit.Load();
            if (k == null) return new JsonOut().Obj().Str("error", "no conveyor kit").ToString();
            return new JsonOut().Obj().Num("minLength", k.minLength).Num("maxLength", k.maxLength).Num("minRadius", k.minRadius)
                .Num("maxSlope", k.maxSlope).Num("minHeight", k.minHeight).Num("maxHeight", k.maxHeight).Num("deckHeight", k.deckHeight)
                .Num("pricePerMetre", k.pricePerMetre).Bool("builder", Api.Player != null).ToString();
        }

        // Day/night clock: lunar day length, clock speed and the current sun direction.
        static string Sun()
        {
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonSolarSystem>());
            if (found.Length == 0) return new JsonOut().Obj().Str("error", "no solar system").ToString();
            var s = found[0].Cast<MoonSolarSystem>();
            var dir = s.sunlight != null ? -s.sunlight.transform.forward : Vector3.zero;
            return new JsonOut().Obj().Num("time", Time.time).Num("lunarDaySeconds", s.lunarDaySeconds).Num("clockSpeed", MoonSolarSystem.clockSpeed)
                .Num("clockOffset", MoonSolarSystem.clockOffset).Vec("spinAxis", MoonSolarSystem.SpinAxis).Vec("skySun", s.skySun)
                .Vec("sunlightDir", dir).Num("sunHeight", s.SunHeight).ToString();
        }

        // Recipe definitions for the processing machines.
        static string Specs()
        {
            var o = new JsonOut().Obj().Num("chamberBatchKg", MoonWorld.ChamberBatchKg).Num("pressDustPerBlockKg", MoonWorld.PressDustPerBlockKg)
                .Num("pressSandPerBlockKg", MoonWorld.PressSandPerBlockKg).Num("stoneBlockKg", MoonWorld.StoneBlockKg)
                .Num("chamberPartBatchSeconds", MoonWorld.ChamberPartBatchSeconds).Num("beltSpeed",
                    MoonWorld.BeltSpeed).Num("beltSpacing", MoonWorld.BeltSpacing)
                .Arr("machines");
            foreach (var kind in new[] { 96, 97 })
            {
                var s = MoonWorld.Processor(kind);
                if (s == null) { o.Null(null); continue; }
                o.Obj().Num("kind", kind).Str("name", s.name).Str("product", s.product).Num("runningKw", s.runningKw).Num("cycleSeconds", s.cycleSeconds)
                 .Num("output", s.output).Arr("slots");
                for (int k = 0; s.slotNames != null && k < s.slotNames.Length; k++)
                    o.Obj().Str("name", s.slotNames[k]).Str("cargo", s.slotCargo[k].ToString()).Num("capacity", s.slotCapacity[k]).End();
                o.End().End();
            }
            return o.ToString();
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
            var o = new JsonOut().Obj().Vec("centre", c).Num("half", half).Num("step", step).Num("n", n).Arr("h");
            var terrain = w.Terrain;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    var p = c + east * (-half + i * step) + north * (half - j * step);
                    o.Num(Vector3.Dot(terrain.SurfacePoint(p.normalized) - c, up));
                }
            return o.ToString();
        }
    }
}
