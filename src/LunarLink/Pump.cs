// The frame pump: the one MonoBehaviour. It waits for the game's loading screen, answers queued requests on the main
// thread (at most ~8 ms a frame), drives the hot plugins, and keeps the request journal and the stuck-frame watcher.
using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    public class Pump : MonoBehaviour
    {
        public Pump(IntPtr ptr) : base(ptr) { }
        static MoonWorld world;

        // At most ~8 ms of request work per frame so the game never stalls; the rest waits for later frames.
        // Requests are answered as soon as the world exists (it is looked for once a second until then).
        static float worldSince = -1f, nextLook = 0f;
        void Update()
        {
            Plugin.Frames++;
            Mark("pump: frame " + Plugin.Frames + " (game frame " + Time.frameCount + ") update start"); if (Time.frameCount <= TraceUntil) Journal("pump: update start");
            if (mainThread < 0) { mainThread = Thread.CurrentThread.ManagedThreadId; Journal("pump: first frame"); }
            if (Plugin.Frames % 300 == 0) { Journal("pump: alive, " + Plugin.Pending.Count + " queued, " + Plugin.Served + " served"); NoteForSupport(); }
            Support.LastFrameTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (worldSince < 0f)
            {
                if (Time.realtimeSinceStartup < nextLook) return;
                nextLook = Time.realtimeSinceStartup + 1f;
                if (World() == null) return;
                // the world object exists before the game has finished loading (its start-up dust check and settling run
                // after it). Answering requests or patching game code in that window froze the game (2026-11-02, and three
                // times on 2026-11-05/06), so nothing happens until the game's own loading screen is gone.
                if (Loading()) return;
                worldSince = Time.realtimeSinceStartup;
                Plugin.L.LogInfo("game loaded (loading screen gone); answering requests");
                Journal("game loaded");
                Support.WorldLoaded = true;
                try { Support.PlayerName = Steamworks.SteamFriends.GetPersonaName(); } catch { }     // blanked out in support files
                TraceUntil = Time.frameCount + 5;
                // nothing is patched in here: twice (2026-11-06 19:00, 19:15) the game locked inside its own
                // MoonWorld.UpdateProgress (start-up clean-section check) right after perf timers were patched in at this
                // point. The hot plugins (LunarLink's modules among them) start now, after loading.
                Journal("pump: game loaded; tracing every patched call until frame " + TraceUntil);
                Hot.StartAll();
            }
            Trace("pump: requests");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 8 && Plugin.Pending.TryDequeue(out var job))
            {
                TraceUntil = Math.Max(TraceUntil, Time.frameCount + 5);
                Journal("pump: dequeued a request; tracing every patched call until frame " + TraceUntil);
                string body; int code = 200;
                var one = System.Diagnostics.Stopwatch.StartNew();
                string what = job.Req.Path + (job.Req.Query != null && job.Req.Query.Count > 0 ? "?" + job.Req.Query : "");
                Journal("start " + what);
                try { body = Route(job.Req); }
                catch (Exception e) { code = 500; body = "{\"error\":" + J.S(e.ToString()) + "}"; }
                Journal("done  " + what + " " + one.ElapsedMilliseconds + " ms");
                Plugin.Served++;
                if (one.ElapsedMilliseconds > 50)
                    Plugin.L.LogInfo($"{job.Req.Path} took {one.ElapsedMilliseconds} ms");
                var client = job.Client;
                Journal("pump: handing reply to sender (" + body.Length + " chars)");
                System.Threading.Tasks.Task.Run(() => Plugin.Send(client, code, body));
                Journal("pump: reply handed off");
            }
            if (worldSince > 0f) Hot.Update();
            Trace("pump: update end");
        }

        void LateUpdate()
        {
            if (worldSince < 0f) return;
            Hot.LateUpdate();
        }

        // The game's loading screen (MoonLoadingScreen.Active) - true until loading has really finished.
        static System.Reflection.PropertyInfo loadingActive; static bool loadingLooked;
        internal static bool Loading()
        {
            try
            {
                if (!loadingLooked)
                {
                    loadingLooked = true;
                    loadingActive = typeof(MoonCleaners.MoonLoadingScreen).GetProperty("Active", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (loadingActive == null) Plugin.L.LogWarning("MoonLoadingScreen.Active not found: requests start when the world exists");
                }
                return loadingActive != null && (bool)loadingActive.GetValue(null);
            }
            catch (Exception e) { Plugin.L.LogWarning("loading check: " + e.Message); loadingActive = null; return false; }
        }

        // Every request, written and flushed to BepInEx/plugins/LunarLink/requests.log as it starts and finishes: after
        // a freeze its last line names the request that was running (or shows that none was).
        static string journalPath; internal static int mainThread = -1;
        // every patched call is journalled before and after while Time.frameCount <= TraceUntil (the frames right after patching)
        internal static int TraceUntil = -1;
        internal static void Trace(string line) { Mark(line); if (Time.frameCount <= TraceUntil) Journal(line); }

        // Flight recorder: every step and every patched call (main thread) goes into this ring, always. A watcher thread
        // writes the last 400 entries to the journal as soon as the main thread stops finishing frames for 3 s, so
        // a lock-up shows exactly what was running - "start X" without "done X" is where it is stuck.
        const int RingSize = 8192;
        static readonly string[] ring = new string[RingSize]; static readonly long[] ringAt = new long[RingSize]; static int ringPos;
        internal static void Mark(string what) { int i = ringPos & (RingSize - 1); ring[i] = what; ringAt[i] = System.Diagnostics.Stopwatch.GetTimestamp(); ringPos++; }
        internal static void StartWatcher()
        {
            new Thread(() =>
            {
                int lastFrames = -1; long since = System.Diagnostics.Stopwatch.GetTimestamp(); bool dumped = false;
                while (true)
                {
                    Thread.Sleep(250);
                    int f = Plugin.Frames; long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (f != lastFrames)
                    {
                        if (dumped) Journal("watch: main thread moving again after " + ((now - since) * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms");
                        lastFrames = f; since = now; dumped = false; continue;
                    }
                    if (dumped || mainThread < 0 || (now - since) < 3 * System.Diagnostics.Stopwatch.Frequency) continue;
                    dumped = true;
                    int end = ringPos, n = Math.Min(400, Math.Min(end, RingSize));
                    var sb = new StringBuilder("watch: MAIN THREAD STUCK - no frame finished for 3 s after frame " + f + "; its last " + n + " steps (ms before now):\n");
                    for (int k = end - n; k < end; k++)
                    {
                        int i = k & (RingSize - 1);
                        sb.Append("    -").Append((now - ringAt[i]) * 1000 / System.Diagnostics.Stopwatch.Frequency).Append(" ms  ").Append(ring[i]).Append('\n');
                    }
                    Journal(sb.ToString());
                }
            }) { IsBackground = true, Name = "LunarLink watcher" }.Start();
        }
        static readonly object journalLock = new object();
        internal static void Journal(string line)
        {
            lock (journalLock) try
            {
                if (journalPath == null)
                {
                    journalPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "requests.log");
                    var old = journalPath + ".1"; if (System.IO.File.Exists(journalPath)) { System.IO.File.Copy(journalPath, old, true); System.IO.File.Delete(journalPath); }
                }
                string f; try { f = Thread.CurrentThread.ManagedThreadId == mainThread ? "f" + Time.frameCount : "t" + Thread.CurrentThread.ManagedThreadId; } catch { f = "?"; }
                System.IO.File.AppendAllText(journalPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + f + " " + line + "\n");
            }
            catch { }
        }

        // every 300 frames: the frame rate and the game's heap, for the support file
        static float noteAt = -1f;
        static void NoteForSupport()
        {
            float now = Time.realtimeSinceStartup;
            if (noteAt > 0f && now > noteAt) Support.Fps = 300 / (now - noteAt);
            noteAt = now;
            try { Support.GameHeapMB = IL2CPP.il2cpp_gc_get_used_size() / 1048576.0; } catch { }
        }

        internal static MoonWorld World()
        {
            if (world == null)
            {
                var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonWorld>());
                if (found.Length > 0) world = found[0].Cast<MoonWorld>();
            }
            return world;
        }

        // stamped by build.sh (the build time); /status reports it so a client can tell which build the game loaded
        static string Status() => "{\"ok\":true,\"version\":" + J.S(PluginVersion) + ",\"world\":" + (World() != null ? "true" : "false") + ",\"time\":" + J.F(Time.time)
            + ",\"queued\":" + Plugin.Pending.Count + ",\"served\":" + Plugin.Served + ",\"frames\":" + Plugin.Frames + "}";
        internal static void Routes()
        {
            Api.Get("/status", "Is LunarLink up: its build, whether a world is loaded, game time, queue and frames", r => Status());
            Api.Get("/support", "Write a support file (BepInEx/lunarlink-support-<time>.zip) to attach to a GitHub issue: what failed, logs, plugins, settings, names blanked out", r => Support.Http(false));
        }

        internal static readonly string PluginVersion = typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

        // Every route is in the table (Api): the core's (/status, /hot/*) and the modules'. A path no route has
        // goes to a hot plugin's HandleHttp, then a normal BepInEx plugin's (a POST to a plugin only with allow-writes).
        static string Route(Req req)
        {
            var path = req.Path.TrimEnd('/'); if (path == "") path = "/status";
            // the core's own routes answer without a world; the modules' and plugins' need one
            if (World() == null && !Api.IsCore(path)) return "{\"error\":\"no world loaded yet\"}";
            var answer = Api.Dispatch(req, path);
            if (answer != null) return answer;
            // a POST passed on to a plugin's HandleHttp is a change: it needs allow-writes like every module's changes
            if (req.Method == "POST") { var no = Api.Writes(); if (no != null) return no; }
            return Hot.Http(req.Method, path, req.Query, req.Body) ?? PluginHttp.Http(req.Method, path, req.Query, req.Body) ?? Api.Unknown(path);
        }
    }

}
