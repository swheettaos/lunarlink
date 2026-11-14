using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    // MoonFramework's PowerSystem.Link (once a second: which solar grid each framework machine is on) measured every
    // machine against every panel and asked the game for every panel's position each time - 873 x ~1300, ~80 ms a run,
    // a hitch every second (2026-11-07). This does the same: nearest panel whose range reaches the machine, its grid -
    // with positions remembered (nothing moves) and panels sorted into cells, so each machine only checks nearby ones.
    // POST /perf/fixes/modify?name=power&on=1|0 switches it live; kept in power_fix.txt and installed once the game has loaded when on.
    // MoonFramework is reached by name (no compile-time reference): LunarLink builds without it, and runs without it.
    static class PowerFix
    {
        public static bool On = Load(), Installed; public static string Reason;
        static FieldInfo gridsF, gridOfF, panelsF; static PropertyInfo recordP;

        // MoonFramework's assembly, when it's loaded
        static Assembly Framework()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "MoonFramework") return a;
            throw new InvalidOperationException("MoonFramework isn't loaded");
        }
        static readonly Dictionary<int, (Vector3 pos, float range)> panelPos = new();
        static readonly Dictionary<object, (int id, Vector3 pos)> machinePos = new(ReferenceEqualityComparer.Instance);
        static readonly Dictionary<int, float> rangeOf = new();
        public static int Runs; public static double LastMs, TotalMs; static long origTicks; static int origRuns;

        static string Path => System.IO.Path.Combine(Api.DataDir, "power_fix.txt");
        static bool Load() { try { return System.IO.File.ReadAllText(Path).Trim() == "1"; } catch { return false; } }
        public static string Set(bool on)
        {
            On = on; try { System.IO.File.WriteAllText(Path, on ? "1" : "0"); } catch { }
            if (!Installed) Install();
            return Json();
        }

        public static void Install()
        {
            if (Installed) return; Installed = true;
            try
            {
                var fw = Framework();
                var ps = fw.GetType("MoonFramework.Internal.PowerSystem", true);
                recordP = fw.GetType("MoonFramework.Machine", true).GetProperty("Record");      // the game's MoonDepotSnapshot
                gridsF = ps.GetField("grids", BindingFlags.Static | BindingFlags.NonPublic);
                gridOfF = ps.GetField("gridOf", BindingFlags.Static | BindingFlags.NonPublic);
                panelsF = ps.GetNestedType("Grid", BindingFlags.NonPublic).GetField("Panels");
                var link = ps.GetMethod("Link", BindingFlags.Static | BindingFlags.NonPublic);
                Pump.Journal("power: patching PowerSystem.Link");
                new Harmony(PerfModule.Id + ".power").Patch(link,
                    prefix: new HarmonyMethod(typeof(PowerFix).GetMethod(nameof(LinkPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(PowerFix).GetMethod(nameof(LinkPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Pump.Journal("power: Link patched, fast link " + (On ? "on" : "off"));
            }
            catch (Exception e) { Reason = e.Message; Pump.Journal("power: patch failed: " + e.Message); }
        }

        static long t0;
        static bool LinkPrefix(MoonWorld world, IEnumerable machines)
        {
            t0 = Stopwatch.GetTimestamp();
            if (!On || Reason != null) return true;
            try { FastLink(world, machines); }
            catch (Exception e) { Reason = e.Message; On = false; Pump.Journal("power: fast link failed, mod's own from now on: " + e); return true; }
            LastMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; TotalMs += LastMs; Runs++;
            return false;
        }
        static void LinkPostfix(bool __runOriginal)
        {
            if (!__runOriginal) return;
            origTicks += Stopwatch.GetTimestamp() - t0; origRuns++;
        }

        static void FastLink(MoonWorld world, IEnumerable machines)
        {
            var gridOf = (Dictionary<int, int>)gridOfF.GetValue(null);
            var grids = (IDictionary)gridsF.GetValue(null);
            gridOf.Clear();
            // the panels with their range and grid, in cells as big as the longest range
            var panels = new List<(Vector3 pos, float range, int grid)>(); float maxRange = 1f;
            foreach (DictionaryEntry kv in grids)
            {
                int gid = (int)kv.Key;
                foreach (var o in (IList)panelsF.GetValue(kv.Value))
                {
                    var st = (MoonProductionState)o; int id = st.Id;
                    if (!panelPos.TryGetValue(id, out var pp))
                    {
                        if (!world.TryGetStructure(id, out var rec) || rec == null) continue;
                        if (!rangeOf.TryGetValue(rec.kind, out var r)) rangeOf[rec.kind] = r = MoonWorld.PowerRange(rec.kind);
                        panelPos[id] = pp = (new Vector3(rec.x, rec.y, rec.z), r);
                    }
                    panels.Add((pp.pos, pp.range, gid)); if (pp.range > maxRange) maxRange = pp.range;
                }
            }
            var cells = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Cell(Vector3 p) => ((int)Math.Floor(p.x / maxRange), (int)Math.Floor(p.y / maxRange), (int)Math.Floor(p.z / maxRange));
            for (int i = 0; i < panels.Count; i++)
            {
                var c = Cell(panels[i].pos);
                if (!cells.TryGetValue(c, out var l)) cells[c] = l = new List<int>();
                l.Add(i);
            }
            foreach (var m in machines)
            {
                if (!machinePos.TryGetValue(m, out var mp)) { var r = (MoonDepotSnapshot)recordP.GetValue(m); machinePos[m] = mp = (r.id, new Vector3(r.x, r.y, r.z)); }
                var (cx, cy, cz) = Cell(mp.pos); float best = float.MaxValue; int bestIdx = int.MaxValue;
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                {
                    if (!cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var l)) continue;
                    foreach (var i in l)
                    {
                        float d = Vector3.Distance(mp.pos, panels[i].pos);
                        // same choice as the mod: nearest within range; on an exact tie, the panel it would have met first
                        if (d <= panels[i].range && (d < best || (d == best && i < bestIdx))) { best = d; bestIdx = i; }
                    }
                }
                if (bestIdx != int.MaxValue) gridOf[mp.id] = panels[bestIdx].grid;
            }
        }

        // ---- capsule offers (MoonFramework Intake -> Machines.Offer -> the Quantum adapter -> every output on its channel)
        // A capsule at a full line was offered again every frame - ~80 of ~86 offers a frame refused, each one a full try of
        // every output (2026-11-07). A refused capsule is not offered again for OfferBackoff frames (1 = every frame, the
        // mod's way); POST /perf/fixes/modify?name=power&backoff=N, kept in offer_backoff.txt.
        public static int OfferBackoff = LoadBackoff();
        static readonly Dictionary<int, int> refusedAt = new();
        public static long OffersRun, OffersSkipped, OffersTaken; static int lastSweep;
        static string BackoffPath => System.IO.Path.Combine(Api.DataDir, "offer_backoff.txt");
        static int LoadBackoff() { try { return Math.Max(1, int.Parse(System.IO.File.ReadAllText(BackoffPath).Trim())); } catch { return 1; } }
        public static string SetBackoff(int n)
        {
            OfferBackoff = Math.Max(1, n); try { System.IO.File.WriteAllText(BackoffPath, OfferBackoff.ToString()); } catch { }
            if (!offerInstalled) InstallOffer();
            return Json();
        }
        static bool offerInstalled;
        public static void InstallOffer()
        {
            if (offerInstalled) return; offerInstalled = true;
            try
            {
                var mt = Framework().GetType("MoonFramework.Internal.Machines", true);
                var offer = mt.GetMethod("Offer", BindingFlags.Static | BindingFlags.NonPublic);
                Pump.Journal("power: patching Machines.Offer");
                new Harmony(PerfModule.Id + ".offers").Patch(offer,
                    prefix: new HarmonyMethod(typeof(PowerFix).GetMethod(nameof(OfferPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(PowerFix).GetMethod(nameof(OfferPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Pump.Journal("power: Machines.Offer patched, back-off " + OfferBackoff + " frames");
            }
            catch (Exception e) { Reason = e.Message; Pump.Journal("power: offer patch failed: " + e.Message); }
        }
        static bool OfferPrefix(int capsuleId, ref bool __result)
        {
            int f = Time.frameCount;
            if (f - lastSweep > 600) { lastSweep = f; var old = new List<int>(); foreach (var kv in refusedAt) if (f - kv.Value > 600) old.Add(kv.Key); foreach (var k in old) refusedAt.Remove(k); }
            if (OfferBackoff > 1 && refusedAt.TryGetValue(capsuleId, out var at) && f - at < OfferBackoff) { OffersSkipped++; __result = false; return false; }
            OffersRun++; return true;
        }
        static void OfferPostfix(int capsuleId, bool __result, bool __runOriginal)
        {
            if (!__runOriginal) return;
            if (__result) { refusedAt.Remove(capsuleId); OffersTaken++; } else refusedAt[capsuleId] = Time.frameCount;
        }

        public static string Json() =>
            "{\"ok\":" + (Reason == null ? "true" : "false") + ",\"on\":" + (On ? "true" : "false") + ",\"installed\":" + (Installed ? "true" : "false")
            + ",\"fastRuns\":" + Runs + ",\"fastLastMs\":" + J.F((float)LastMs) + ",\"fastAvgMs\":" + J.F(Runs > 0 ? (float)(TotalMs / Runs) : 0f)
            + ",\"modRuns\":" + origRuns + ",\"modAvgMs\":" + J.F(origRuns > 0 ? (float)(origTicks * 1000.0 / Stopwatch.Frequency / origRuns) : 0f)
            + ",\"offerBackoff\":" + OfferBackoff + ",\"offersRun\":" + OffersRun + ",\"offersSkipped\":" + OffersSkipped + ",\"offersTaken\":" + OffersTaken
            + ",\"reason\":" + J.S(Reason) + "}";
    }
}
