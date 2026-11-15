using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace LunarLink
{
    // The report's own parts: the summary (what's wrong first, then the setup and the moment), the hot and BepInEx
    // plugins, and every DLL with its date and hash.
    static partial class Support
    {
        static readonly List<string> warnings = new List<string>();     // warnings by source, this support file's

        static string Summary(List<string> problems)
        {
            double ago = LastFrameTicks > 0 ? (Stopwatch.GetTimestamp() - LastFrameTicks) / (double)Stopwatch.Frequency : -1;
            if (ago > 3) problems.Insert(0, "The game's main thread hasn't finished a frame for " + ago.ToString("0")
                + " s: the game is frozen (logs/lunarlink-stuck.txt shows what it was doing)");
            var sb = new StringBuilder();
            sb.AppendLine("LunarLink support file, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine();
            sb.AppendLine("== What looks wrong");
            if (problems.Count == 0) sb.AppendLine("Nothing it can see: no failed plugins, no errors in the logs, no freezes or crashes.");
            foreach (var p in problems) sb.AppendLine("- " + p);
            if (warnings.Count > 0)
            {
                sb.AppendLine().AppendLine("== Warnings (often harmless; logs/bepinex-errors.txt has them with the lines round them)");
                foreach (var warning in warnings) sb.AppendLine("- " + warning);
            }
            sb.AppendLine().AppendLine("== Setup");
            sb.AppendLine("LunarLink        " + Pump.PluginVersion + "   (installed-version: "
                + (ReadText(Path.Combine(Api.DataDir, "installed-version")) ?? "?").Trim() + ")");
            sb.AppendLine("Game             " + gameVersion + "   Unity " + unityVersion);
            sb.AppendLine("BepInEx          " + Try(() => typeof(BepInEx.Paths).Assembly.GetName().Version + " / " + (typeof(BepInEx.Paths).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),
                    false).FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion));
            sb.AppendLine("System           " + os + (Wine() is string w ? "   (Wine/Proton " + w + ")" : ""));
            sb.AppendLine(".NET             " + RuntimeInformation.FrameworkDescription);
            sb.AppendLine("CPU              " + cpu + ", " + cores + " threads");
            sb.AppendLine("RAM              " + ramMB + " MB");
            sb.AppendLine("Graphics         " + gpu + ", " + vramMB + " MB, " + graphics);
            sb.AppendLine("Writes allowed   " + (File.Exists(Path.Combine(Api.DataDir, "allow-writes")) ? "yes (allow-writes file)" : "no"));
            sb.AppendLine().AppendLine("== The moment");
            var up = DateTime.Now - started;
            sb.AppendLine("Running for      " + (int)up.TotalHours + " h " + up.Minutes + " min");
            sb.AppendLine("World loaded     " + (WorldLoaded ? "yes" : "no (main menu, or still loading)"));
            if (ago >= 0) sb.AppendLine("Last frame       " + ago.ToString("0.0") + " s ago" + (ago > 3 ? "   <- the game's main thread looks STUCK" : ""));
            if (Fps >= 0) sb.AppendLine("Frame rate       " + Fps.ToString("0.0") + " fps");
            if (GameHeapMB >= 0) sb.AppendLine("Game heap        " + GameHeapMB.ToString("0") + " MB (IL2CPP)");
            Try(() =>
            {
                var g = GC.GetGCMemoryInfo();
                sb.AppendLine(".NET heap        " + (GC.GetTotalMemory(false) >> 20) + " MB; collections " + GC.CollectionCount(0)
                    + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2) + "; last pause "
                    + g.PauseDurations.ToArray().Sum(d => d.TotalMilliseconds).ToString("0.0") + " ms");
                using var p = Process.GetCurrentProcess();
                sb.AppendLine("Process          " + (p.WorkingSet64 >> 20) + " MB in use"
                    + (p.PrivateMemorySize64 > 0 ? ", " + (p.PrivateMemorySize64 >> 20) + " MB private" : "") + ", "
                    + p.Threads.Count + " threads");
                return "";
            });
            sb.AppendLine("Requests served  " + Plugin.Served + ", " + Plugin.Pending.Count + " waiting");
            sb.AppendLine().AppendLine("== In this zip");
            sb.AppendLine("plugins/       hot plugins, BepInEx plugins (and dependency errors), every DLL, the updater");
            sb.AppendLine("logs/          errors with the lines round them, and the start and end of each log, for this run");
            sb.AppendLine("previous-run/  the same for the run before (after a crash, look here)");
            sb.AppendLine("crashes/       Unity's crash reports, if any");
            sb.AppendLine("config/        LunarLink's and BepInEx's settings (access key blanked)");
            sb.AppendLine().AppendLine("Blanked out: players' names, Steam ids, the user and PC name, LAN addresses, the access key.");
            return sb.ToString();
        }

        // ---- plugins ----

        static void HotProblems(List<string> problems)
        {
            Try(() =>
            {
                foreach (var p in Hot.Snapshot())
                {
                    if (p.error != null) problems.Add("Hot plugin " + p.name + " failed: " + p.error);
                    if (p.leftovers > 0) problems.Add("Hot plugin " + p.name + " left things behind when it stopped ("
                        + p.leftovers + "): see plugins/hot-plugins.json");
                    if (p.newer) problems.Add("Hot plugin " + p.name + " has a newer build on disk that isn't loaded");
                }
                return "";
            });
        }

        static string BepPlugins(List<string> problems)
        {
            return Try(() =>
            {
                var sb = new StringBuilder("Loaded BepInEx plugins (name, version, GUID, file):\n");
                var cl = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance;
                foreach (var kv in cl.Plugins.OrderBy(k => k.Value.Metadata.Name))
                {
                    var m = kv.Value.Metadata;
                    sb.Append("  ").Append(m.Name).Append("   ").Append(m.Version).Append("   ").Append(m.GUID).Append("   ")
                      .Append(string.IsNullOrEmpty(kv.Value.Location) ? "?" : Path.GetRelativePath(BepRoot, kv.Value.Location))
                      .Append(kv.Value.Instance == null ? "   (NOT LOADED)" : "").Append('\n');
                    if (kv.Value.Instance == null) problems.Add("BepInEx plugin " + m.Name + " " + m.Version
                        + " was found but didn't load: see logs/bepinex-errors.txt");
                }
                var errs = cl.DependencyErrors;
                sb.Append("\nDependency errors (BepInEx skipped these plugins):\n");
                if (errs == null || errs.Count == 0) sb.Append("  none\n");
                else foreach (var e in errs) { sb.Append("  ").Append(e).Append('\n'); problems.Add("BepInEx: " + e); }
                return sb.ToString();
            });
        }

        static string Dlls()
        {
            return Try(() =>
            {
                var sb = new StringBuilder("Every DLL under BepInEx/plugins, BepInEx/hot and BepInEx/patchers (size, date, first 12 of SHA-256):\n");
                foreach (var dir in new[] { "plugins", "hot", "patchers" })
                {
                    var d = Path.Combine(BepRoot, dir); if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".dll")
                        || f.EndsWith(".disabled") || f.EndsWith(".off")).OrderBy(f => f))
                    {
                        string hash = Try(() => { using var s = File.OpenRead(f);
                            return Convert.ToHexString(System.Security.Cryptography.SHA256.Create().ComputeHash(s))[..12].ToLowerInvariant(); });
                        sb.Append("  ").Append(Path.GetRelativePath(BepRoot, f)).Append("   ").Append(new FileInfo(f).Length).Append(" bytes   ")
                          .Append(File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm")).Append("   ").Append(hash).Append('\n');
                    }
                }
                return sb.ToString();
            });
        }

        // ---- logs ----
    }
}
