// After a reload, a plugin whose new copy keeps failing (Mars Sweepers' second copy couldn't load its content and threw
// on every frame, 1 fps) is stopped instead of left to spam: for 10 s after a reload, every error in the BepInEx log
// whose text names the plugin's code is counted, and at 20 the plugin is stopped (off until the next start, when it
// loads cleanly) with "restart the game" on its row. Counting happens wherever the log is written; stopping on the main
// thread (Hot.Update).
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace LunarLink
{
    static class ReloadWatch
    {
        const float Seconds = 10f; const int Limit = 20;
        static readonly object gate = new object();
        static readonly Dictionary<string, (string code, float until, int errors)> watched = new Dictionary<string, (string, float, int)>();
        static bool listening;

        // a reload is starting: watch this plugin's errors for a while (its code is named "<assembly>." in a stack)
        internal static void Begin(Hot.Plugin p)
        {
            if (!listening) { listening = true; try { BepInEx.Logging.Logger.Listeners.Add(new Listener()); } catch { } }
            var code = p.Asm?.GetName().Name ?? p.Name;
            lock (gate) watched[p.Name] = (code + ".", Time.realtimeSinceStartup + Seconds, 0);
        }

        sealed class Listener : ILogListener
        {
            public LogLevel LogLevelFilter => LogLevel.Error | LogLevel.Fatal;
            public void LogEvent(object sender, LogEventArgs e)
            {
                if ((e.Level & (LogLevel.Error | LogLevel.Fatal)) == 0) return;
                string text; try { text = e.Data?.ToString(); } catch { return; }
                if (string.IsNullOrEmpty(text)) return;
                lock (gate)
                    foreach (var k in watched.Keys.ToList())
                    {
                        var w = watched[k];
                        if (text.Contains(w.code, StringComparison.Ordinal)) watched[k] = (w.code, w.until, w.errors + 1);
                    }
            }
            public void Dispose() { }
        }

        // Each frame (main thread): stop what passed the limit, forget what's been quiet for long enough
        internal static void Check()
        {
            List<(string name, int errors)> failed = null;
            lock (gate)
            {
                if (watched.Count == 0) return;
                float now = Time.realtimeSinceStartup;
                foreach (var k in watched.Keys.ToList())
                {
                    var w = watched[k];
                    if (w.errors >= Limit) { (failed ??= new List<(string, int)>()).Add((k, w.errors)); watched.Remove(k); }
                    else if (now > w.until) watched.Remove(k);
                }
            }
            if (failed == null) return;
            foreach (var (name, errors) in failed) Hot.StopFailed(name, errors);
        }
    }
}
