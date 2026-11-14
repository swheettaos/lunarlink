using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LunarLink
{
    // Stopwatches round the game's own per-frame systems, to see where the main thread goes.
    // /perf: fps and ms per frame of each, since the last /perf (so: read once, wait, read again), the worst single frame
// of each, and every long frame ("blips", over 100 ms): how long, the timed systems that took most of it, and whether
// a garbage collection ran in it (the game's IL2CPP one, and .NET's). Blips are caught with no timers in too.
    // Each timer is a Harmony prefix/postfix; hot per-object ones (MoonMachineBelt) add a little of their own cost.
    static class Perf
    {
        static readonly (string type, string method)[] Targets =
        {
            ("MoonWorld", "Update"), ("MoonWorld", "StepConveyors"), ("MoonWorld", "StepProduction"), ("MoonWorld", "StepPresses"),
            ("MoonWorld", "StepCatchers"), ("MoonWorld", "StepFarRobots"), ("MoonWorld", "UpdateDustBanks"),
            ("MoonWorld", "UpdateCleaningCollisions"), ("MoonWorld", "UpdateLive"), ("MoonWorld", "UpdateProgress"),
            ("MoonWorld", "StepCrewDust"), ("MoonWorld", "StepPackagers"),
            ("MoonConveyorNetwork", "Update"), ("MoonConveyorNetwork", "LateUpdate"), ("MoonMachineBelt", "LateUpdate"),
            ("MoonDustBankGeometry", "UpdatePages"), ("MoonJoonasRig", "Animate"), ("MoonDustFragments", "Update"),
            ("MoonDustStation", "Update"), ("MoonDustCanisterFlight", "Update"),
        };
        static readonly Dictionary<MethodBase, string> name = new();
        static readonly Dictionary<string, long> ticks = new();
        static readonly Dictionary<string, int> calls = new();
        static readonly Dictionary<string, long> frameTicks = new(), worst = new();      // this frame's, and the worst frame's, per system
        static readonly List<string> blips = new();                                      // long frames since the last /perf (JSON each)
        static int gcGame = -1, gcNet = -1; static long gameHeap = -1;

        // Once a frame (PerfModule.Update): what the frame that just ended cost, per system; and a blip if it was long.
        public static void Frame()
        {
            float dt = Time.unscaledDeltaTime;
            int g = -1, n = GC.CollectionCount(0);
            try { g = Il2CppSystem.GC.CollectionCount(0); } catch { }
            long heap = -1; try { heap = Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_get_used_size(); } catch { }
            foreach (var kv in frameTicks) if (!worst.TryGetValue(kv.Key, out var w) || kv.Value > w) worst[kv.Key] = kv.Value;
            if (dt > 0.1f && blips.Count < 60)
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                var top = new List<KeyValuePair<string, long>>(frameTicks); top.Sort((a, b) => b.Value.CompareTo(a.Value));
                var sb = new System.Text.StringBuilder("{\"at\":").Append(J.F(Time.realtimeSinceStartup)).Append(",\"ms\":").Append(J.F(dt * 1000))
                    .Append(",\"gcGame\":").Append(gcGame >= 0 && g >= 0 ? g - gcGame : -1).Append(",\"gcNet\":").Append(gcNet >= 0 ? n - gcNet : -1);
                // the game's heap shrinking means its own collector ran; .NET's last collection, if one ran in this frame
                if (gameHeap > 0 && heap > 0) sb.Append(",\"gameHeapMB\":").Append(J.F(heap / 1048576.0)).Append(",\"gameHeapChangeMB\":").Append(J.F((heap - gameHeap) / 1048576.0));
                if (gcNet >= 0 && n != gcNet)
                {
                    var i = GC.GetGCMemoryInfo(GCKind.Any); double pause = 0; foreach (var pd in i.PauseDurations) pause += pd.TotalMilliseconds;
                    sb.Append(",\"netGc\":{\"generation\":").Append(i.Generation).Append(",\"pauseMs\":").Append(J.F(pause)).Append('}');
                }
                sb.Append(",\"top\":{");
                for (int i = 0; i < top.Count && i < 4; i++) { if (i > 0) sb.Append(','); sb.Append(J.S(top[i].Key)).Append(':').Append(J.F(top[i].Value * ms)); }
                blips.Add(sb.Append("}}").ToString());
            }
            frameTicks.Clear(); gcGame = g; gcNet = n; gameHeap = heap;
        }
        static int frame0 = -1; static float time0;
        public static string Installed = ""; public static bool Main;

        public static void Install(Harmony h)
        {
            var asm = typeof(MoonCleaners.MoonWorld).Assembly; int n = 0; var miss = new List<string>();
            foreach (var (tn, mn) in Targets)
            {
                var t = asm.GetType("MoonCleaners." + tn, false) ?? asm.GetType(tn, false);
                MethodInfo m = null;
                try { m = t?.GetMethod(mn, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch (AmbiguousMatchException) { }
                if (m == null) { miss.Add(tn + "." + mn); continue; }
                try
                {
                    name[m] = tn + "." + mn; startName[m] = "perf: start " + tn + "." + mn; doneName[m] = "perf: done  " + tn + "." + mn;
                    h.Patch(m, prefix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                               postfix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)));
                    n++;
                }
                catch (Exception e) { miss.Add(tn + "." + mn + " (" + e.GetType().Name + ")"); }
            }
            Main = true; Installed = n + " timers" + (miss.Count > 0 ? "; not timed: " + string.Join(", ", miss) : "") + Installed;
            Plugin.L.LogInfo("Perf: " + Installed);
        }

        // /perf/time?type=MoonSolarPanelView&method=Update: one more timer, patched in on request (read back through /perf)
        public static string TimeOne(Harmony h, string tn, string mn)
        {
            var t = FindType(tn);
            if (t == null) return "{\"ok\":false,\"reason\":\"no type " + tn + "\"}";
            MethodInfo m = null; var where = t;                      // the class itself, else the parent class that declares it
            for (; where != null && m == null && where != typeof(MonoBehaviour); where = where.BaseType)
            {
                // "Name" = no parameters; "Name/2" = the overload with 2 parameters
                var parts = mn.Split('/'); int np = parts.Length > 1 && int.TryParse(parts[1], out var pn) ? pn : 0;
                foreach (var c in where.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (c.Name == parts[0] && c.GetParameters().Length == np && !c.IsGenericMethodDefinition) { m = c; break; }
                if (m != null) break;
            }
            if (m == null) return "{\"ok\":false,\"reason\":\"no method " + tn + "." + mn + " in it or its parents\"}";
            if (where != t) tn = where.Name;                        // timed under the class that really has it
            mn = mn.Split('/')[0];
            if (name.ContainsKey(m)) return "{\"ok\":true,\"already\":true}";
            name[m] = tn + "." + mn; startName[m] = "perf: start " + tn + "." + mn; doneName[m] = "perf: done  " + tn + "." + mn;
            Pump.Journal("perf: patching timer on " + tn + "." + mn);
            h.Patch(m, prefix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                       postfix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)));
            Pump.Journal("perf: timer on " + tn + "." + mn + " patched");
            Installed += "; +" + tn + "." + mn;
            return "{\"ok\":true,\"timed\":" + J.S(tn + "." + mn) + "}";
        }

        // a type by full name in any loaded assembly (game, MoonFramework, Quantum Conveyors...), or by short name in the game
        public static Type FindType(string tn)
        {
            var g = typeof(MoonCleaners.MoonWorld).Assembly;
            var t = g.GetType("MoonCleaners." + tn, false) ?? g.GetType(tn, false);
            if (t != null) return t;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = a.GetType(tn, false); } catch { t = null; }
                if (t != null) return t;
            }
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
                foreach (var x in ts) if (x != null && x.Name == tn && !(x.Namespace ?? "").StartsWith("Il2Cpp")) return x;
            }
            return null;
        }

        // /perf/methods?type=X: the methods a class declares (name and parameter count), to pick what to time
        public static string Methods(string tn)
        {
            var t = FindType(tn);
            if (t == null) return "{\"ok\":false,\"reason\":\"no type " + tn + "\"}";
            var sb = new System.Text.StringBuilder("{\"ok\":true,\"type\":").Append(J.S(t.FullName)).Append(",\"assembly\":").Append(J.S(t.Assembly.GetName().Name)).Append(",\"methods\":[");
            bool first = true;
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!first) sb.Append(','); first = false;
                sb.Append(J.S(m.Name + "/" + m.GetParameters().Length + (m.IsStatic ? " static" : "")));
            }
            return sb.Append("]}").ToString();
        }

        static bool firstPost;
        static readonly Dictionary<MethodBase, string> startName = new(), doneName = new();
        static void Pre(out long __state, MethodBase __originalMethod) { Pump.Trace(startName.TryGetValue(__originalMethod, out var sn) ? sn : "perf: start " + __originalMethod.Name); __state = Stopwatch.GetTimestamp(); }
        static void Post(long __state, MethodBase __originalMethod)
        {
            long d = Stopwatch.GetTimestamp() - __state;
            Pump.Trace(doneName.TryGetValue(__originalMethod, out var dn) ? dn : "perf: done  " + __originalMethod.Name);
            if (!firstPost) { firstPost = true; Pump.Journal("perf: first timed call: " + __originalMethod.Name); }
            if (!name.TryGetValue(__originalMethod, out var k)) k = __originalMethod.Name;
            ticks[k] = (ticks.TryGetValue(k, out var t) ? t : 0) + d; calls[k] = (calls.TryGetValue(k, out var c) ? c : 0) + 1;
            frameTicks[k] = (frameTicks.TryGetValue(k, out var ft) ? ft : 0) + d;
        }

        // .NET's garbage collector (BepInEx and every mod's code; not the game's): its heap, how often each generation ran,
        // and the last collection's pause
        static string NetGc()
        {
            try
            {
                var i = GC.GetGCMemoryInfo(GCKind.Any);
                double pause = 0; foreach (var p in i.PauseDurations) pause += p.TotalMilliseconds;
                return "{\"heapMB\":" + J.F(GC.GetTotalMemory(false) / 1048576.0) + ",\"committedMB\":" + J.F(i.TotalCommittedBytes / 1048576.0)
                    + ",\"collections\":[" + GC.CollectionCount(0) + "," + GC.CollectionCount(1) + "," + GC.CollectionCount(2) + "]"
                    + ",\"last\":{\"generation\":" + i.Generation + ",\"pauseMs\":" + J.F(pause) + ",\"compacted\":" + (i.Compacted ? "true" : "false")
                    + ",\"concurrent\":" + (i.Concurrent ? "true" : "false") + ",\"promotedMB\":" + J.F(i.PromotedBytes / 1048576.0) + "}"
                    + ",\"server\":" + (System.Runtime.GCSettings.IsServerGC ? "true" : "false") + ",\"latencyMode\":" + J.S(System.Runtime.GCSettings.LatencyMode.ToString()) + "}";
            }
            catch (Exception e) { return J.S("unavailable: " + e.Message); }
        }

        public static string Json()
        {
            int f = Time.frameCount; float now = Time.realtimeSinceStartup;
            int frames = frame0 < 0 ? 0 : f - frame0; float secs = now - time0;
            var sb = new System.Text.StringBuilder("{\"ok\":true,\"installed\":").Append(J.S(Installed))
                .Append(",\"frames\":").Append(frames).Append(",\"fps\":").Append(J.F(secs > 0 && frames > 0 ? frames / secs : 0))
                .Append(",\"msPerFrame\":{");
            double ms = 1000.0 / Stopwatch.Frequency; bool first = true;
            var keys = new List<string>(ticks.Keys); keys.Sort((a, b) => ticks[b].CompareTo(ticks[a]));
            foreach (var k in keys)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append(J.S(k)).Append(":[").Append(J.F(frames > 0 ? ticks[k] * ms / frames : 0)).Append(',').Append(frames > 0 ? calls[k] / frames : 0).Append(']');
            }
            sb.Append("},\"worstMs\":{"); first = true;
            foreach (var k in keys) { if (!first) sb.Append(','); first = false; sb.Append(J.S(k)).Append(':').Append(J.F(worst.TryGetValue(k, out var w) ? w * ms : 0)); }
            sb.Append("},\"blips\":[").Append(string.Join(",", blips)).Append(']');
            sb.Append(",\"gcNet\":").Append(NetGc());
            try { sb.Append(",\"gameHeapMB\":").Append(J.F(Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_get_used_size() / 1048576.0)); } catch { }
            ticks.Clear(); calls.Clear(); worst.Clear(); blips.Clear(); frame0 = f; time0 = now;
            return sb.Append('}').ToString();
        }
    }
}
