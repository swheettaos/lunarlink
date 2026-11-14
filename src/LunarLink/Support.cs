// The support file: GET /support (or F9's "Support file" button) writes BepInEx/lunarlink-support-<time>.zip, everything
// needed to work out why a plugin failed, crashed or didn't load, with players' names, Steam ids, the user name in
// paths, LAN addresses and the access key blanked out, ready to attach to a GitHub issue.
//   summary.txt          what's wrong first (failed plugins, errors by source), then the setup and the moment
//   plugins/             hot plugins (/hot/list), BepInEx plugins and their dependency errors, every DLL with its date
//                        and hash, the updater's view
//   logs/                errors and warnings with the lines round them, and the start and end of each log: BepInEx's
//                        LogOutput.log, Unity's Player.log, BepInEx's ErrorLog.log, LunarLink's journal (with any
//                        "main thread stuck" dump)
//   previous-run/        the same for the run before this one (a crash's logs survive the restart: BepInEx overwrites
//                        its log at every start, so Keep() follows it into plugins/LunarLink/support/ as it grows)
//   crashes/             Unity's crash reports
//   config/              LunarLink's and BepInEx's settings, the hot plugin list
// It's built in the core and answered on the server's own thread (Plugin.Receive), so it works at the main menu, when
// a module fails to load, and while the game is frozen. Nothing in here touches the game's (IL2CPP) side: what needs the
// main thread is noted by Init() at start and by Pump (Note) while the game runs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace LunarLink
{
    static class Support
    {
        static readonly DateTime started = DateTime.Now;
        static string gameVersion = "?", unityVersion = "?", os = "?", cpu = "?", gpu = "?", graphics = "?", consoleLog, company, product;
        static int cores, ramMB, vramMB;
        // noted on the main thread while the game runs (Pump)
        internal static long LastFrameTicks;              // Stopwatch timestamp of the last frame the main thread finished
        internal static double GameHeapMB = -1, Fps = -1;
        internal static string PlayerName;                // the local player's Steam name: blanked out like the others
        internal static bool WorldLoaded;

        static string BepRoot => BepInEx.Paths.BepInExRootPath;
        static string Mine => Path.Combine(Api.DataDir, "support");
        static string LogOutput => Path.Combine(BepRoot, "LogOutput.log");

        // At LunarLink's Load (main thread): what Unity knows about the machine, and the BepInEx log keeper.
        internal static void Init()
        {
            try { gameVersion = Application.version; } catch { }
            try { unityVersion = Application.unityVersion; } catch { }
            try { company = Application.companyName; product = Application.productName; } catch { }
            try { consoleLog = Application.consoleLogPath; } catch { }
            try { os = SystemInfo.operatingSystem; cpu = SystemInfo.processorType; cores = SystemInfo.processorCount; ramMB = SystemInfo.systemMemorySize; } catch { }
            try { gpu = SystemInfo.graphicsDeviceName; vramMB = SystemInfo.graphicsMemorySize; graphics = SystemInfo.graphicsDeviceType + " (" + SystemInfo.graphicsDeviceVersion + ")"; } catch { }
            Keep();
        }

        // ---- the previous run's BepInEx log ----
        // BepInEx starts LogOutput.log afresh at every start, so after a crash and a restart the crashed run's log is
        // gone. While the game runs, every new part of it is appended to support/LogOutput-this-run.log (every 2 s, only
        // what's new); at the next start that copy becomes LogOutput-previous-run.log.
        static void Keep()
        {
            try
            {
                Directory.CreateDirectory(Mine);
                var now = Path.Combine(Mine, "LogOutput-this-run.log"); var prev = Path.Combine(Mine, "LogOutput-previous-run.log");
                if (File.Exists(now)) File.Move(now, prev, true);
                new Thread(() =>
                {
                    long at = 0;
                    while (true)
                    {
                        Thread.Sleep(2000);
                        try
                        {
                            using var src = new FileStream(LogOutput, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            if (src.Length < at) at = 0;                                        // a new log: start over
                            if (src.Length == at) continue;
                            if (at == 0 && src.Length > 50L << 20) at = src.Length - (50L << 20);  // keep at most the last 50 MB
                            src.Seek(at, SeekOrigin.Begin);
                            using (var dst = new FileStream(now, at == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read)) src.CopyTo(dst);
                            at = src.Length;
                        }
                        catch { }
                    }
                }) { IsBackground = true, Name = "LunarLink log keeper", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
            }
            catch (Exception e) { Plugin.L.LogWarning("support: can't keep a copy of the BepInEx log: " + e.Message); }
        }

        // ---- GET /support ----

        // The answer: JSON, or a short page when a browser asked (html).
        internal static string Http(bool html)
        {
            var (file, problems, error) = Save();
            if (html)
            {
                var sb = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>LunarLink support file</title><style>body{font:16px/1.6 system-ui,sans-serif;max-width:760px;margin:40px auto;padding:0 16px;background:#f6f5f1;color:#1c2230}code{background:#ece9e2;padding:2px 6px;border-radius:4px;word-break:break-all}a{color:#d9601a}li{margin:4px 0}</style></head><body><h1>LunarLink support file</h1>");
                if (error != null) sb.Append("<p><b>It couldn't be written:</b> ").Append(Esc(error)).Append("</p>");
                else
                {
                    sb.Append("<p>Saved to</p><p><code>").Append(Esc(file)).Append("</code></p>")
                      .Append("<p>Attach that file to your issue: <a href=\"https://github.com/swheettaos/lunarlink/issues/new/choose\">github.com/swheettaos/lunarlink/issues</a> (drag it into the issue's text box). Names, Steam ids, the user name in paths, LAN addresses and the access key are blanked out; you can open the zip to check what's in it.</p>");
                    sb.Append("<h2>What it found</h2><ul>");
                    foreach (var p in problems.DefaultIfEmpty("Nothing wrong that it can see.")) sb.Append("<li>").Append(Esc(p)).Append("</li>");
                    sb.Append("</ul>");
                }
                return sb.Append("</body></html>").ToString();
            }
            if (error != null) return "{\"ok\":false,\"error\":" + J.S(error) + "}";
            return "{\"ok\":true,\"file\":" + J.S(file) + ",\"problems\":[" + string.Join(",", problems.Select(J.S)) + "],\"attach\":" +
                J.S("Attach this file to your issue at https://github.com/swheettaos/lunarlink/issues/new/choose") + "}";
        }
        static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        static readonly object gate = new object();

        // Write the zip; (its path, what looks wrong, an error if it couldn't be written).
        internal static (string file, List<string> problems, string error) Save()
        {
            lock (gate)
            {
                var problems = new List<string>(); warnings.Clear();
                try
                {
                    var names = Names();
                    var files = new List<(string name, string text)>();
                    void Add(string name, string text) { if (text != null) files.Add((name, Blank(text, names))); }

                    // plugins
                    string hot = Try(() => Hot.ListJson());
                    Add("plugins/hot-plugins.json", hot);
                    Add("plugins/bepinex-plugins.txt", BepPlugins(problems));
                    Add("plugins/dll-files.txt", Dlls());
                    Add("plugins/updates.json", Try(() => Updates.Status?.Invoke() ?? "{\"updater\":\"off (the LunarLink.Update module isn't running)\"}"));
                    HotProblems(problems);

                    // this run's logs
                    var bep = ReadLines(LogOutput);
                    Add("logs/bepinex-errors.txt", Errors(bep, BepError, problems, "BepInEx log"));
                    Add("logs/bepinex-start.txt", Head(bep, 400));
                    Add("logs/bepinex-end.txt", Tail(bep, 1500));
                    var unity = ReadLines(consoleLog);
                    Add("logs/game-errors.txt", Errors(unity, GameError, problems, "game's log (Player.log)"));
                    Add("logs/game-end.txt", Tail(unity, 1000));
                    var errLog = ReadLines(Path.Combine(BepRoot, "ErrorLog.log"));
                    Add("logs/bepinex-ErrorLog-end.txt", Tail(errLog, 500));
                    var journal = ReadLines(Path.Combine(Api.DataDir, "requests.log"));
                    Add("logs/lunarlink-journal-end.txt", Tail(journal, 1500));
                    Add("logs/lunarlink-stuck.txt", Stuck(journal, problems, "this run"));

                    // the previous run (a crash shows here after the restart)
                    var bepPrev = ReadLines(Path.Combine(Mine, "LogOutput-previous-run.log"));
                    Add("previous-run/bepinex-errors.txt", Errors(bepPrev, BepError, null, null));
                    Add("previous-run/bepinex-start.txt", Head(bepPrev, 400));
                    Add("previous-run/bepinex-end.txt", Tail(bepPrev, 1500));
                    var unityPrev = consoleLog == null ? null : ReadLines(Path.Combine(Path.GetDirectoryName(consoleLog), "Player-prev.log"));
                    Add("previous-run/game-errors.txt", Errors(unityPrev, GameError, null, null));
                    Add("previous-run/game-end.txt", Tail(unityPrev, 1000));
                    var journalPrev = ReadLines(Path.Combine(Api.DataDir, "requests.log.1"));
                    Add("previous-run/lunarlink-journal-end.txt", Tail(journalPrev, 800));
                    Add("previous-run/lunarlink-stuck.txt", Stuck(journalPrev, problems, "the previous run"));
                    if (unityPrev != null && unityPrev.Any(l => l.Contains("Crash!!!"))) problems.Add("The previous run crashed (Unity's log says Crash!!!): see previous-run/ and crashes/");

                    Crashes(Add, problems);

                    // settings
                    Add("config/lunarlink.cfg", ReadText(Path.Combine(BepRoot, "config", "lunarlink.cfg")));
                    Add("config/BepInEx.cfg", ReadText(Path.Combine(BepRoot, "config", "BepInEx.cfg")));
                    Add("config/hot.json", ReadText(Path.Combine(Hot.Dir, "hot.json")));
                    Add("config/config-files.txt", Try(() => string.Join("\n", Directory.GetFiles(Path.Combine(BepRoot, "config"), "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(BepRoot, f) + "   " + new FileInfo(f).Length + " bytes   " + File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm")))));

                    files.Insert(0, ("summary.txt", Blank(Summary(problems), names)));

                    var path = Path.Combine(BepRoot, "lunarlink-support-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
                    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                        foreach (var (name, text) in files)
                            using (var w = new StreamWriter(zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal).Open(), new UTF8Encoding(false))) w.Write(text);
                    // only the last five support files are kept
                    foreach (var old in Directory.GetFiles(BepRoot, "lunarlink-support-*.zip").OrderByDescending(f => f).Skip(5)) Try(() => { File.Delete(old); return ""; });
                    Plugin.L.LogInfo("support file written: " + path);
                    return (path, problems, null);
                }
                catch (Exception e) { return (null, problems, e.GetType().Name + ": " + e.Message); }
            }
        }

        // ---- summary ----

        static readonly List<string> warnings = new List<string>();     // warnings by source, this support file's

        static string Summary(List<string> problems)
        {
            double ago = LastFrameTicks > 0 ? (Stopwatch.GetTimestamp() - LastFrameTicks) / (double)Stopwatch.Frequency : -1;
            if (ago > 3) problems.Insert(0, "The game's main thread hasn't finished a frame for " + ago.ToString("0") + " s: the game is frozen (logs/lunarlink-stuck.txt shows what it was doing)");
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
            sb.AppendLine("LunarLink        " + Pump.PluginVersion + "   (installed-version: " + (ReadText(Path.Combine(Api.DataDir, "installed-version")) ?? "?").Trim() + ")");
            sb.AppendLine("Game             " + gameVersion + "   Unity " + unityVersion);
            sb.AppendLine("BepInEx          " + Try(() => typeof(BepInEx.Paths).Assembly.GetName().Version + " / " + (typeof(BepInEx.Paths).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion));
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
                sb.AppendLine(".NET heap        " + (GC.GetTotalMemory(false) >> 20) + " MB; collections " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2) + "; last pause " + g.PauseDurations.ToArray().Sum(d => d.TotalMilliseconds).ToString("0.0") + " ms");
                using var p = Process.GetCurrentProcess();
                sb.AppendLine("Process          " + (p.WorkingSet64 >> 20) + " MB in use" + (p.PrivateMemorySize64 > 0 ? ", " + (p.PrivateMemorySize64 >> 20) + " MB private" : "") + ", " + p.Threads.Count + " threads");
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
                    if (p.leftovers > 0) problems.Add("Hot plugin " + p.name + " left things behind when it stopped (" + p.leftovers + "): see plugins/hot-plugins.json");
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
                    if (kv.Value.Instance == null) problems.Add("BepInEx plugin " + m.Name + " " + m.Version + " was found but didn't load: see logs/bepinex-errors.txt");
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
                    foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".dll") || f.EndsWith(".disabled") || f.EndsWith(".off")).OrderBy(f => f))
                    {
                        string hash = Try(() => { using var s = File.OpenRead(f); return Convert.ToHexString(System.Security.Cryptography.SHA256.Create().ComputeHash(s))[..12].ToLowerInvariant(); });
                        sb.Append("  ").Append(Path.GetRelativePath(BepRoot, f)).Append("   ").Append(new FileInfo(f).Length).Append(" bytes   ")
                          .Append(File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm")).Append("   ").Append(hash).Append('\n');
                    }
                }
                return sb.ToString();
            });
        }

        // ---- logs ----

        static readonly Regex BepLevel = new Regex(@"^\[(Error|Fatal|Warning)\s*:\s*([^\]]*?)\s*\]", RegexOptions.Compiled);
        static bool BepError(string l) => l.StartsWith("[Error") || l.StartsWith("[Fatal") || l.StartsWith("[Warning") || l.Contains("Exception:") || l.StartsWith("   at ");
        static bool GameError(string l) => l.Contains("Exception") || l.Contains("Crash!!!") || l.StartsWith("ERROR") || l.Contains("Error:") || l.Contains("Fatal");

        // Every line the test picks, with 12 lines before and after (overlaps merged), at most 4000 lines; problems
        // (when given) get a count of errors and warnings by source.
        static string Errors(List<string> lines, Func<string, bool> pick, List<string> problems, string what)
        {
            if (lines == null) return null;
            var hits = new List<int>();
            for (int i = 0; i < lines.Count; i++) if (pick(lines[i])) hits.Add(i);
            if (problems != null && what != null)
            {
                var bySource = new Dictionary<string, int>();
                var warnBy = new Dictionary<string, int>();
                foreach (var i in hits)
                {
                    var m = BepLevel.Match(lines[i]); if (!m.Success) continue;
                    var into = m.Groups[1].Value == "Warning" ? warnBy : bySource; var k = m.Groups[2].Value;
                    into[k] = into.TryGetValue(k, out var n) ? n + 1 : 1;
                }
                foreach (var kv in bySource.OrderByDescending(k => k.Value)) problems.Add(kv.Value + " error" + (kv.Value == 1 ? "" : "s") + " from " + kv.Key + " in the " + what);
                foreach (var kv in warnBy.OrderByDescending(k => k.Value)) warnings.Add(kv.Value + " warning" + (kv.Value == 1 ? "" : "s") + " from " + kv.Key + " in the " + what);
                if (what.StartsWith("game")) { int ex = lines.Count(l => l.Contains("Exception")); if (ex > 0) problems.Add(ex + " exception line" + (ex == 1 ? "" : "s") + " in the " + what); }
            }
            if (hits.Count == 0) return "No errors or warnings.\n";
            var sb = new StringBuilder(); int shown = 0, last = -1;
            foreach (var i in hits)
            {
                int a = Math.Max(Math.Max(0, i - 12), last + 1), b = Math.Min(lines.Count - 1, i + 12);
                if (a > b) continue;
                if (last >= 0 && a > last + 1) sb.Append("----- line ").Append(a + 1).Append(" -----\n");
                for (int k = a; k <= b; k++) sb.Append(lines[k]).Append('\n');
                shown += b - a + 1; last = b;
                if (shown > 4000) { sb.Append("\n... more errors after this; see the whole log\n"); break; }
            }
            return sb.ToString();
        }

        // the journal's "main thread stuck" dumps, whole
        static string Stuck(List<string> lines, List<string> problems, string when)
        {
            if (lines == null) return null;
            var sb = new StringBuilder(); int dumps = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].Contains("MAIN THREAD STUCK")) continue;
                dumps++; sb.Append(lines[i]).Append('\n');
                for (int k = i + 1; k < lines.Count && k < i + 420 && lines[k].StartsWith("    "); k++) sb.Append(lines[k]).Append('\n');
                sb.Append('\n');
            }
            if (dumps == 0) return null;
            problems.Add("The game froze " + dumps + " time" + (dumps == 1 ? "" : "s") + " in " + when + " (3 s or more without a frame): see " + (when == "this run" ? "logs" : "previous-run") + "/lunarlink-stuck.txt");
            return sb.ToString();
        }

        static void Crashes(Action<string, string> add, List<string> problems)
        {
            Try(() =>
            {
                var dir = Path.Combine(Path.GetTempPath(), company ?? "", product ?? "", "Crashes");
                if (!Directory.Exists(dir)) return "";
                var all = Directory.GetDirectories(dir).OrderByDescending(Directory.GetLastWriteTime).ToList();
                if (all.Count == 0) return "";
                add("crashes/list.txt", string.Join("\n", all.Select(d => Path.GetFileName(d) + "   " + Directory.GetLastWriteTime(d).ToString("yyyy-MM-dd HH:mm:ss"))));
                foreach (var d in all.Take(3))
                {
                    var e = Path.Combine(d, "error.log");
                    if (File.Exists(e)) add("crashes/" + Path.GetFileName(d) + "-error.log", Tail(ReadLines(e), 400));
                }
                var newest = Directory.GetLastWriteTime(all[0]);
                if (DateTime.Now - newest < TimeSpan.FromDays(2)) problems.Add("Unity wrote a crash report at " + newest.ToString("yyyy-MM-dd HH:mm") + ": see crashes/");
                return "";
            });
        }

        // a log, read while it's still being written (the last 30 MB at most)
        static List<string> ReadLines(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (s.Length > 30L << 20) s.Seek(-(30L << 20), SeekOrigin.End);
                using var r = new StreamReader(s);
                var lines = new List<string>(); string l;
                while ((l = r.ReadLine()) != null) lines.Add(l);
                return lines;
            }
            catch { return null; }
        }
        static string ReadText(string path) { var l = ReadLines(path); return l == null ? null : string.Join("\n", l); }
        static string Head(List<string> l, int n) => l == null ? null : string.Join("\n", l.Take(n)) + (l.Count > n ? "\n... (" + (l.Count - n) + " more lines)" : "");
        static string Tail(List<string> l, int n) => l == null ? null : (l.Count > n ? "... (" + (l.Count - n) + " earlier lines)\n" : "") + string.Join("\n", l.Skip(Math.Max(0, l.Count - n)));

        // ---- blanking ----

        // players' names from every "<Steam id> | <name> | ..." list in BepInEx/config (Crew Tools', Crew Permissions'),
        // the local player's Steam name, the PC's name
        static List<string> Names()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            Try(() =>
            {
                foreach (var f in Directory.GetFiles(Path.Combine(BepRoot, "config"), "*players*.txt"))
                    foreach (var l in File.ReadAllLines(f))
                    {
                        var parts = l.Split('|');
                        if (parts.Length >= 2 && Regex.IsMatch(parts[0].Trim(), @"^\d{15,20}$")) names.Add(parts[1].Trim());
                    }
                return "";
            });
            if (!string.IsNullOrWhiteSpace(PlayerName)) names.Add(PlayerName.Trim());
            Try(() => { names.Add(Environment.MachineName); return ""; });
            return names.Where(n => n.Length >= 3).OrderByDescending(n => n.Length).ToList();
        }

        static readonly Regex SteamId = new Regex(@"\b7656119\d{10}\b", RegexOptions.Compiled),
            UserPath = new Regex(@"(?i)([\\/])(home|users)([\\/])(?!public\b|steamuser\b|default\b)([^\\/\s""':|]+)", RegexOptions.Compiled),
            Lan = new Regex(@"\b(10\.\d{1,3}|192\.168|172\.(1[6-9]|2\d|3[01])|100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])|169\.254)\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled),
            AnyIp = new Regex(@"(?<=(://|from |refused |address |Address = |on ))(?!127\.0\.0\.1\b|0\.0\.0\.0\b)\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled),
            KeyLine = new Regex(@"(?im)^(Access key\s*=\s*).+$", RegexOptions.Compiled),
            KeyQuery = new Regex(@"(?i)([?&]key=)[^&\s""]+", RegexOptions.Compiled);

        internal static string Blank(string text, List<string> names)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!string.IsNullOrEmpty(Plugin.AccessKey)) text = text.Replace(Plugin.AccessKey, "[key]");
            text = KeyLine.Replace(text, "$1[key]");
            text = KeyQuery.Replace(text, "$1[key]");
            text = SteamId.Replace(text, "[steamid]");
            text = UserPath.Replace(text, "$1$2$3[user]");
            text = Lan.Replace(text, "[lan-ip]");
            text = AnyIp.Replace(text, "[ip]");
            for (int i = 0; i < names.Count; i++)
            {
                var n = names[i]; var pat = Regex.Escape(n);
                if (char.IsLetterOrDigit(n[0])) pat = @"(?<![\w])" + pat;
                if (char.IsLetterOrDigit(n[^1])) pat += @"(?![\w])";
                text = Regex.Replace(text, pat, "[player " + (i + 1) + "]");
            }
            return text;
        }

        // ---- small things ----

        static string Try(Func<string> f) { try { return f(); } catch (Exception e) { return "(couldn't read: " + e.GetType().Name + ": " + e.Message + ")"; } }

        // Wine's version when the game runs under Wine or Proton (ntdll's wine_get_version), else null
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr WineVersion();
        static string Wine()
        {
            try
            {
                var ntdll = GetModuleHandle("ntdll.dll"); if (ntdll == IntPtr.Zero) return null;
                var f = GetProcAddress(ntdll, "wine_get_version"); if (f == IntPtr.Zero) return null;
                return Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<WineVersion>(f)());
            }
            catch { return null; }
        }
    }
}
