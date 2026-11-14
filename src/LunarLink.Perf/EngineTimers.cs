using System;
using UnityEngine;

namespace LunarLink
{
    // Unity's own frame counters (whole frame, scripts, physics, rendering, GPU wait, draw calls). Kept apart from Perf so
    // nothing here is touched at load: the recorders start only on POST /perf/engine?on=1 (started at load, the game froze, 2026-11-06).
    static class EngineTimers
    {
        static readonly (string cat, string name, string label)[] Markers =
        {
            ("Internal", "Main Thread", "main thread (whole frame)"), ("Internal", "PlayerLoop", "player loop"),
            ("Scripts", "Update.ScriptRunBehaviourUpdate", "scripts: Update"), ("Scripts", "PreLateUpdate.ScriptRunBehaviourLateUpdate", "scripts: LateUpdate"),
            ("Scripts", "FixedUpdate.ScriptRunBehaviourFixedUpdate", "scripts: FixedUpdate"), ("Physics", "FixedUpdate.PhysicsFixedUpdate", "physics"),
            ("Animation", "PreLateUpdate.DirectorUpdateAnimationBegin", "animation begin"), ("Animation", "PreLateUpdate.DirectorUpdateAnimationEnd", "animation end"),
            ("Render", "PostLateUpdate.FinishFrameRendering", "rendering (main thread)"), ("Render", "Gfx.WaitForPresentOnGfxThread", "waiting on the GPU"),
            ("Internal", "Gfx.WaitForGfxCommandsFromMainThread", "render thread waiting on main"),
            ("Render", "Batches Count", "#batches"), ("Render", "SetPass Calls Count", "#setpass calls"), ("Render", "Draw Calls Count", "#draw calls"), ("Render", "Vertices Count", "#vertices"),
        };
        static Unity.Profiling.ProfilerRecorder[] rec;
        static Unity.Profiling.ProfilerCategory Cat(string n) => n switch
        {
            "Render" => Unity.Profiling.ProfilerCategory.Render, "Scripts" => Unity.Profiling.ProfilerCategory.Scripts,
            "Physics" => Unity.Profiling.ProfilerCategory.Physics, "Animation" => Unity.Profiling.ProfilerCategory.Animation,
            _ => Unity.Profiling.ProfilerCategory.Internal,
        };
        public static void Start()
        {
            if (rec != null) return;
            Pump.Trace("engine timers: starting " + Markers.Length + " recorders");
            rec = new Unity.Profiling.ProfilerRecorder[Markers.Length];
            for (int i = 0; i < Markers.Length; i++)
            {
                Pump.Trace("engine timers: starting " + Markers[i].name);
                try { rec[i] = Unity.Profiling.ProfilerRecorder.StartNew(Cat(Markers[i].cat), Markers[i].name, 1, Unity.Profiling.ProfilerRecorderOptions.Default); }
                catch (Exception e) { Plugin.L.LogWarning("perf marker " + Markers[i].name + ": " + e.Message); }
                Pump.Trace("engine timers: started " + Markers[i].name);
            }
            Pump.Trace("engine timers: all started");
        }
        // each frame's last values are added up (PerfModule.Update calls Sample once a frame), so a read is the average per
        // frame since the previous read - one frame's value alone jumped between 54 and 154 ms (2026-11-07)
        static double[] sum; static int samples;
        // the recorders cost a little every frame, so they run only between POST /perf/engine?on=1 and ?on=0 (or the module stopping)
        public static void Stop()
        {
            if (rec == null) return;
            for (int i = 0; i < rec.Length; i++) try { rec[i].Dispose(); } catch { }
            rec = null; sum = null; samples = 0;
            Pump.Journal("engine timers: stopped");
        }
        internal static bool Running => rec != null;

        public static void Sample()
        {
            if (rec == null) return;
            if (sum == null) sum = new double[Markers.Length];
            for (int i = 0; i < Markers.Length; i++) try { if (rec[i].Valid) sum[i] += rec[i].LastValue; } catch { }
            samples++;
        }
        public static string Json()
        {
            if (rec == null) return "{}";
            Pump.Trace("engine timers: reading");
            var sb = new System.Text.StringBuilder("{\"frames\":").Append(samples);
            for (int i = 0; i < Markers.Length; i++)
            {
                string v = "null";
                if (sum != null && samples > 0) { double x = sum[i] / samples; v = Markers[i].label.StartsWith("#") ? ((long)x).ToString() : J.F((float)(x / 1e6)); }
                sb.Append(',').Append(J.S(Markers[i].label)).Append(':').Append(v);
            }
            if (sum != null) Array.Clear(sum, 0, sum.Length); samples = 0;
            return sb.Append('}').ToString();
        }

    }
}
