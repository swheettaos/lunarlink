using System;
using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    // Changing the world: structures and belts (check, create, remove), loose capsules, and the player's own build tool.
    static class BuildArea
    {
        internal static void Start(string state)
        {
            Api.Ask("/structures/check", "Body {kind, pos:[x,y,z], q:[x,y,z,w], support, raised, free, snapped}: the game's own placement check; builds nothing", r => Structure(Api.World, r.Body, false));
            Api.Post("/structures/create", "Same body: build it with the game's TryBuildStructure (the player within about 22 m). Returns its id", r => Structure(Api.World, r.Body, true));
            Api.Post("/structures/remove", "?ids=1,2,3[&refund=0][&robot=1] : out of the world from anywhere, refund paid unless refund=0; robots only with robot=1. One at a time with a gap", r => RemoveStructures(Api.World, r));
            Api.Ask("/belts/check", "Body {start, startDir, end, endDir, startStructure, endStructure, startBelt, endBelt}: can this belt be built, its length and price", r => CheckBelt(Api.World, r.Body));
            Api.Post("/belts/create", "Same body: build the belt (the player within about 22 m). Returns its id", r => BuildBelt(Api.World, r.Body));
            Api.Post("/belts/remove", "?ids=1,2,3 : belts out with the game's undo, from anywhere, refunded", r => RemoveBelts(Api.World, r));
            Api.Post("/canisters/remove", "?id= : delete one loose capsule (not riding, carried or flying)", r => RemoveCanister(Api.World, r));
            Api.Get("/builder", "The player's build tool: placing, ghost kind, valid, reason, plan, what overlaps the preview", r => BuilderState());
            Api.Post("/builder/aim", "Body {kind, from:[x,y,z], to:[x,y,z], wheel, free}: pick a kind and aim the real build tool", r => BuilderAim(r.Body));
            Api.Post("/builder/place", "Press place on the aimed build tool", r => BuilderPlace());
            Api.Post("/builder/stop", "Put the build tool down", r => BuilderStop());
        }

        internal static void Stop() { try { if (depotBuilder != null) BuilderStop(); } catch { } }

        // the build tool's "place" press lasts placeFrames frames
        internal static void Update()
        {
            var tdb = depotBuilder;
            if (tdb != null && tdb.TestInput != null)
            {
                var ti = tdb.TestInput;
                if (placeFrames > 0) { ti.place = true; placeFrames--; } else ti.place = false;
                tdb.TestInput = ti;
            }
        }

        // Stacking test (2026-11-02): a full structure plan - kind, 3D position, rotation (quaternion), the
        // structure it stands on (support), raised/free/snapped - checked with the game's own multi-level check,
        // or built with its own TryBuildStructure. POST {kind, pos:[x,y,z], q:[x,y,z,w], support, raised, free,
        // snapped}; build: create it (otherwise only the check)
        static string Structure(MoonWorld w, string body, bool build)
        {
            var b = Api.Player;
            if (b == null) return Api.NoPlayer;
            var e = System.Text.Json.JsonDocument.Parse(body).RootElement;
            var p = new MoonWorld.StructurePlan();
            p.kind = I(e, "kind"); p.support = I(e, "support");
            p.position = V3(e, "pos", Vector3.zero);
            if (e.TryGetProperty("q", out var q) && q.GetArrayLength() == 4)
                p.rotation = new Quaternion(q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle(), q[3].GetSingle());
            else p.rotation = Quaternion.identity;
            p.raised = Flag(body, "raised"); p.free = Flag(body, "free"); p.snapped = Flag(body, "snapped");
            if (e.TryGetProperty("hint", out var hv)) p.hint = hv.GetString();
            string reason = null; bool ok;
            // the game's own placement check (what the blue preview waits for), stepped here until it is done
            var chk = w.BeginStructureCheck(p);
            for (int i = 0; i < 400 && chk != null && !chk.Done; i++) chk.Advance(50.0);
            string ck = chk == null ? "null" : "{\"done\":" + (chk.Done ? "true" : "false") + ",\"valid\":" + (chk.Valid ? "true" : "false") + ",\"reason\":" + J.S(chk.Reason) + "}";
            if (build)
            {
                int before = w.nextDepotId;
                ok = w.TryBuildStructure(b, p, chk, out reason);
                return Api.Result(ok, reason, ",\"id\":" + (ok ? before : -1) + ",\"check\":" + ck);
            }
            ok = chk != null && chk.Done && chk.Valid; reason = chk?.Reason;
            string ml = null; bool mok = w.MultiLevelPlanValid(p, out ml);
            return Api.Result(ok, reason, ",\"check\":" + ck + ",\"multiLevel\":" + (mok ? "true" : "false") + ",\"multiLevelReason\":" + J.S(ml));
        }

        // Driving the player's own build tool (2026-11-02): pick an item, aim it like the camera would, let the
        // game make its placement preview (stacking on foundations/posts included), then press "place".
        // POST /builder/aim {kind, from:[x,y,z], to:[x,y,z], wheel, free}; POST /builder/place; POST /builder/stop; GET /builder
        static MoonDepotBuilder depotBuilder;
        internal static int placeFrames;
        internal static MoonDepotBuilder DB()
        {
            if (depotBuilder == null)
            {
                var f = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonDepotBuilder>());
                if (f.Length > 0) depotBuilder = f[0].Cast<MoonDepotBuilder>();
            }
            return depotBuilder;
        }
        static string BuilderState()
        {
            var db = DB(); if (db == null) return "{\"error\":\"no build tool\"}";
            var p = db.structurePlan;
            // each field on its own: one null one (module, cell) used to blank the whole plan
            var parts = new System.Collections.Generic.List<string>(); Vector3 ppos = Vector3.zero; Quaternion prot = Quaternion.identity; bool havePos = false;
            void Add(string name, Func<string> f) { try { parts.Add("\"" + name + "\":" + f()); } catch { } }
            if (p != null)
            {
                Add("kind", () => p.kind.ToString()); Add("support", () => p.support.ToString());
                try { ppos = p.position; prot = p.rotation; havePos = true; } catch { }
                if (havePos) { parts.Add("\"pos\":" + J.V(ppos)); parts.Add("\"q\":[" + J.F(prot.x) + "," + J.F(prot.y) + "," + J.F(prot.z) + "," + J.F(prot.w) + "]"); }
                Add("raised", () => p.raised ? "true" : "false"); Add("snapped", () => p.snapped ? "true" : "false");
                Add("hint", () => J.S(p.hint.ToString())); Add("cell", () => J.S(p.cell.ToString())); Add("joined", () => J.S(p.joined.ToString()));
            }
            string plan = "{" + string.Join(",", parts) + "}";
            // ?overlaps: every collider inside a foundation-sized box at the preview - what "overlaps another tile" means
            string over = "null";
            if (havePos)
            {
                try
                {
                    var up = prot * Vector3.up; var sb = new StringBuilder("["); int n = 0;
                    foreach (var off in new[] { 0.5f, -0.5f })
                    {
                        var hits = Physics.OverlapBox(ppos + up * off, new Vector3(3.9f, 0.45f, 3.9f), prot);
                        for (int i = 0; hits != null && i < hits.Length && n < 16; i++)
                        {
                            var c = hits[i]; if (c == null) continue; var go = c.gameObject; var root = c.transform.root;
                            if (n++ > 0) sb.Append(",");
                            sb.Append("{\"box\":" + J.F(off) + ",\"name\":" + J.S(go.name) + ",\"root\":" + J.S(root != null ? root.name : null)
                                + ",\"layer\":" + go.layer + ",\"trigger\":" + (c.isTrigger ? "true" : "false") + ",\"pos\":" + J.V(c.transform.position)
                                + ",\"bounds\":" + J.V(c.bounds.size) + "}");
                        }
                    }
                    over = sb.Append("]").ToString();
                }
                catch (Exception ex) { over = J.S("overlap check failed: " + ex.Message); }
            }
            return "{\"placing\":" + (db.IsPlacing ? "true" : "false") + ",\"ghostKind\":" + db.ghostKind + ",\"valid\":" + (db.PlacementValid ? "true" : "false")
                + ",\"reason\":" + J.S(db.PlacementReason) + ",\"plan\":" + plan + ",\"overlaps\":" + over + "}";
        }
        static string BuilderAim(string body)
        {
            var db = DB(); if (db == null) return "{\"error\":\"no build tool\"}";
            var e = System.Text.Json.JsonDocument.Parse(body).RootElement;
            int kind = I(e, "kind");
            if (!db.IsPlacing || db.ghostKind != kind) { db.SetOpen(true); db.BeginPlacementFromShop(kind); }
            var from = V3(e, "from", Vector3.zero); var to = V3(e, "to", Vector3.zero);
            var t = new MoonDepotBuilder.TestBuildInput();
            t.aim = new Ray(from, (to - from).normalized);
            t.wheel = e.TryGetProperty("wheel", out var wv) ? wv.GetSingle() : 0f;
            t.free = Flag(body, "free"); t.place = false;
            db.TestInput = t;
            return BuilderState();
        }
        static string BuilderPlace()
        {
            var db = DB(); if (db == null || db.TestInput == null) return "{\"error\":\"aim first\"}";
            placeFrames = 1; return "{\"ok\":true,\"pressed\":true}";
        }
        static string BuilderStop()
        {
            var db = DB(); if (db == null) return "{\"error\":\"no build tool\"}";
            db.TestInput = null; placeFrames = 0;
            try { db.CancelPlacement(); } catch { }
            return "{\"ok\":true}";
        }

        static Vector3 V3(System.Text.Json.JsonElement e, string name, Vector3 dflt)
        {
            if (!e.TryGetProperty(name, out var a) || a.GetArrayLength() != 3) return dflt;
            return new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
        }
        static int I(System.Text.Json.JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetInt32() : -1;

        static MoonWorld.ConveyorPlan Plan(string body)
        {
            var e = System.Text.Json.JsonDocument.Parse(body).RootElement;
            var p = new MoonWorld.ConveyorPlan();
            p.start = V3(e, "start", Vector3.zero); p.startDirection = V3(e, "startDir", Vector3.forward);
            p.end = V3(e, "end", Vector3.zero); p.endDirection = V3(e, "endDir", Vector3.forward);
            p.startStructure = I(e, "startStructure"); p.endStructure = I(e, "endStructure");
            p.startBelt = I(e, "startBelt"); p.endBelt = I(e, "endBelt"); p.replaces = I(e, "replaces");
            p.startUp = V3(e, "startUp", p.start.normalized); p.endUp = V3(e, "endUp", p.end.normalized);
            return p;
        }

        // POST {start,startDir,end,endDir,startStructure,endStructure,startBelt,endBelt}
        static string CheckBelt(MoonWorld w, string body)
        {
            var b = Api.Player;
            if (b == null) return Api.NoPlayer;
            var plan = Plan(body);
            bool ok = w.CheckConveyor(b, plan, out string reason, out MoonConveyorPath path, out Il2CppSystem.Decimal price, out bool routable);
            return "{\"ok\":" + (ok ? "true" : "false") + ",\"reason\":" + J.S(reason) + ",\"routable\":" + (routable ? "true" : "false")
                + ",\"length\":" + (path != null ? J.F(path.Length) : "null") + ",\"price\":" + J.S(price.ToString()) + "}";
        }


        static bool Flag(string body, string name)
        {
            try { return System.Text.Json.JsonDocument.Parse(body).RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True; }
            catch { return false; }
        }
        static string BuildBelt(MoonWorld w, string body)
        {
            var b = Api.Player;
            if (b == null) return Api.NoPlayer;
            bool ok = w.TryBuildConveyor(b, Plan(body), out string reason, out int id);
            return Api.Result(ok, reason, ",\"id\":" + id);
        }

        // ?ids=1,2,3[&refund=0][&robot=1] : straight out of the world, from anywhere, the player not moved (2026-11-09,
        // tearing down every base). The game's instant removal, and unless refund=0 each piece's normal game refund
        // (StructureRefund) is paid into the team money. J.O.O.N.A.S. (kind 99) only with robot=1. Callers remove one at
        // a time with a gap (removing many at once froze the game).
        static string RemoveStructures(MoonWorld w, Req req)
        {
            var ids = Api.Ids(req, "ids");
            bool refund = req.Query["refund"] != "0", robots = req.Query["robot"] == "1";
            var kinds = new System.Collections.Generic.Dictionary<int, int>(); foreach (var kv in w.depots) kinds[kv.Key] = kv.Value.kind;
            int n = 0, skipped = 0, missing = 0; double pay = 0;
            foreach (var i in ids)
            {
                if (!kinds.TryGetValue(i, out var kind)) { missing++; continue; }
                if (kind == 99 && !robots) { skipped++; continue; }
                if (refund) { try { pay += Il2CppSystem.Decimal.ToDouble(w.StructureRefund(i)); } catch { } }
                w.RemoveStructureNow(i); n++;
            }
            if (pay > 0)
            {
                try { var me = Steamworks.SteamUser.GetSteamID().m_SteamID.ToString(); w.wallets.set_Item(me, new Il2CppSystem.Decimal(Il2CppSystem.Decimal.ToDouble(w.wallets.Balance) + pay)); }
                catch (Exception e) { Plugin.L.LogWarning("structure refund: " + e.Message); }
            }
            int left = 0; foreach (var kv in w.depots) if (ids.Contains(kv.Key)) left++;
            Plugin.L.LogInfo($"remove structures: {n} removed, {skipped} robots skipped, {missing} not there, ${pay:0} refunded");
            return "{\"ok\":" + (left == skipped ? "true" : "false") + ",\"removed\":" + n + ",\"skipped\":" + skipped + ",\"missing\":" + missing
                + ",\"left\":" + left + ",\"refund\":" + J.F(pay) + "}";
        }

        // ?ids=1,2,3 : belts out with the game's own undo (UndoConveyors), which works from anywhere and refunds itself.
        static string RemoveBelts(MoonWorld w, Req req)
        {
            var b = Api.Player; if (b == null) return Api.NoPlayer;
            var ids = Api.Ids(req, "ids");
            double before = 0; try { before = Il2CppSystem.Decimal.ToDouble(w.wallets.Balance); } catch { }
            var list = new Il2CppSystem.Collections.Generic.List<int>(); var live = new System.Collections.Generic.HashSet<int>();
            foreach (var kv in w.conveyors) live.Add(kv.Key);
            foreach (var i in ids) if (live.Contains(i)) list.Add(i);
            bool ok = list.Count == 0 || w.UndoConveyors(b, list.Cast<Il2CppSystem.Collections.Generic.IList<int>>(), out Il2CppSystem.Decimal back);
            int left = 0; foreach (var kv in w.conveyors) if (ids.Contains(kv.Key)) left++;
            double after = 0; try { after = Il2CppSystem.Decimal.ToDouble(w.wallets.Balance); } catch { }
            return "{\"ok\":" + (ok && left == 0 ? "true" : "false") + ",\"asked\":" + list.Count + ",\"missing\":" + (ids.Count - list.Count)
                + ",\"left\":" + left + ",\"refund\":" + J.F(after - before) + "}";
        }

        // ?id= : remove one loose capsule (POST /canisters/remove). Looks it up by walking the canister dictionary (the same way /canisters
        // does) instead of TryGetCanister, whose struct out-parameter crashed the game twice. Each step is logged
        // first so a crash can be pinned to the exact call.
        static string RemoveCanister(MoonWorld w, Req req)
        {
            int id = Api.Qi(req, "id", -1);
            double kg = -1;
            foreach (var kv in w.canisters) if (kv.Key == id) { kg = kv.Value.kilograms; break; }
            if (kg < 0) return "{\"ok\":false,\"reason\":\"no such canister\"}";
            Plugin.L.LogInfo($"remove canister {id}: checking state");
            if (w.IsCanisterRiding(id) || w.IsCanisterCarried(id) || w.IsCanisterFlying(id))
                return "{\"ok\":false,\"reason\":\"canister is riding, carried or flying\"}";
            Plugin.L.LogInfo($"remove canister {id}: RemoveCanister");
            w.RemoveCanister(id);
            Plugin.L.LogInfo($"remove canister {id}: done");
            return "{\"ok\":true,\"kg\":" + J.F(kg) + "}";
        }

    }
}
