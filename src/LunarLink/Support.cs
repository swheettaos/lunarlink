// The support file: GET /support (or F9's "Support file" button) writes BepInEx/lunarlink-support-<time>.zip, everything
// needed to work out why a plugin failed, crashed or didn't load, with players' names, Steam ids, the user name in
// paths, LAN addresses and the access key blanked out, ready to attach to a GitHub issue.
//   summary.txt          what's wrong first (failed plugins, errors by source), then the setup and the moment
//   plugins/             hot plugins (/hot/list), BepInEx plugins and their dependency errors, every DLL with its date
//                        and hash, the updater's view
//   logs/                errors and warnings with the lines round them, and the start and end of each log: BepInEx's
//                        LogOutput.log, Unity's Player.log, BepInEx's ErrorLog.log, LunarLink's journal (with any
//                        "main thread stuck" dump), and every error LunarLink ignored on purpose, where and how often
//   previous-run/        the same for the run before this one (a crash's logs survive the restart: BepInEx overwrites
//                        its log at every start, so Keep() follows it into plugins/LunarLink/support/ as it grows)
//   crashes/             Unity's crash reports
//   config/              LunarLink's and BepInEx's settings, the hot plugin list
// It's built in the core and answered on the server's own thread (Plugin.Receive), so it works at the main menu, when
// a module fails to load, and while the game is frozen. Nothing in here touches the game's (IL2CPP) side: what needs the
// main thread is noted by Init() at start and by Pump (Note) while the game runs.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace LunarLink
{
    static partial class Support
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
            try { gameVersion = Application.version; } catch (System.Exception caught) { Quiet.Note(caught); }
            try { unityVersion = Application.unityVersion; } catch (System.Exception caught) { Quiet.Note(caught); }
            try { company = Application.companyName; product = Application.productName; } catch (System.Exception caught) { Quiet.Note(caught); }
            try { consoleLog = Application.consoleLogPath; } catch (System.Exception caught) { Quiet.Note(caught); }
            try { os = SystemInfo.operatingSystem; cpu = SystemInfo.processorType; cores = SystemInfo.processorCount;
                ramMB = SystemInfo.systemMemorySize; } catch (System.Exception caught) { Quiet.Note(caught); }
            try { gpu = SystemInfo.graphicsDeviceName; vramMB = SystemInfo.graphicsMemorySize;
                graphics = SystemInfo.graphicsDeviceType + " (" + SystemInfo.graphicsDeviceVersion + ")"; }
            catch (System.Exception caught) { Quiet.Note(caught); }
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
                            using (var dst = new FileStream(now, at == 0 ? FileMode.Create
                                : FileMode.Append, FileAccess.Write, FileShare.Read)) src.CopyTo(dst);
                            at = src.Length;
                        }
                        catch (System.Exception caught) { Quiet.Note(caught); }
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
                var sb = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>LunarLink support " +
                    "file</title><style>body{font:16px/1.6 system-ui,sans-serif;max-width:760px;margin:40px auto;padding:0 " +
                    "16px;background:#f6f5f1;color:#1c2230}code{background:#ece9e2;padding:2px " +
                    "6px;border-radius:4px;word-break:break-all}a{color:#d9601a}li{margin:4px " +
                    "0}</style></head><body><h1>LunarLink support file</h1>");
                if (error != null) sb.Append("<p><b>It couldn't be written:</b> ").Append(Esc(error)).Append("</p>");
                else
                {
                    sb.Append("<p>Saved to</p><p><code>").Append(Esc(file)).Append("</code></p>")
                      .Append("<p>Attach that file to your issue: <a " +
                          "href=\"https://github.com/swheettaos/lunarlink/issues/new/choose\">github.com/swheettaos/lunarlink/issues</a> " +
                          "(drag it into the issue's text box). Names, Steam ids, the user name in paths, LAN addresses and the access key " +
                          "are blanked out; you can open the zip to check what's in it.</p>");
                    sb.Append("<h2>What it found</h2><ul>");
                    foreach (var p in problems.DefaultIfEmpty("Nothing wrong that it can see.")) sb.Append("<li>").Append(Esc(p)).Append("</li>");
                    sb.Append("</ul>");
                }
                return sb.Append("</body></html>").ToString();
            }
            if (error != null) return new JsonOut().Obj().Bool("ok", false).Str("error", error);
            var o = new JsonOut().Obj().Bool("ok", true).Str("file", file).Arr("problems");
            foreach (var p in problems) o.Str(p);
            return o.End().Str("attach", "Attach this file to your issue at https://github.com/swheettaos/lunarlink/issues/new/choose");
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
                    Add("plugins/updates.json",
                        Try(() => Updates.Status?.Invoke()
                        ?? new JsonOut().Obj().Str("updater", "off (the LunarLink.Update module isn't running)")));
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
                    // errors LunarLink ignored on purpose this run, where and how often (each is in the BepInEx log once)
                    Add("logs/lunarlink-ignored.txt", string.Join("\n", Quiet.Counts().Select(q => q.count.ToString().PadLeft(8) + "  " + q.at)));
                    if (unityPrev != null
                        && unityPrev.Any(l => l.Contains("Crash!!!"))) problems.Add("The previous run crashed (Unity's log " +
                            "says Crash!!!): see previous-run/ and crashes/");

                    Crashes(Add, problems);

                    // settings
                    Add("config/lunarlink.cfg", ReadText(Path.Combine(BepRoot, "config", "lunarlink.cfg")));
                    Add("config/BepInEx.cfg", ReadText(Path.Combine(BepRoot, "config", "BepInEx.cfg")));
                    Add("config/hot.json", ReadText(Path.Combine(Hot.Dir, "hot.json")));
                    Add("config/config-files.txt",
                        Try(() => string.Join("\n",
                        Directory.GetFiles(Path.Combine(BepRoot, "config"), "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(BepRoot, f) + "   " + new FileInfo(f).Length + " bytes   "
                            + File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm")))));

                    files.Insert(0, ("summary.txt", Blank(Summary(problems), names)));

                    var path = Path.Combine(BepRoot, "lunarlink-support-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
                    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                        foreach (var (name, text) in files)
                            using (var w = new StreamWriter(zip.CreateEntry(name,
                                System.IO.Compression.CompressionLevel.Optimal).Open(), new UTF8Encoding(false))) w.Write(text);
                    // only the last five support files are kept
                    foreach (var old in Directory.GetFiles(BepRoot,
                        "lunarlink-support-*.zip").OrderByDescending(f => f).Skip(5)) Try(() => { File.Delete(old); return ""; });
                    Plugin.L.LogInfo("support file written: " + path);
                    return (path, problems, null);
                }
                catch (Exception e) { return (null, problems, e.GetType().Name + ": " + e.Message); }
            }
        }

        // ---- summary ----

        static string Try(Func<string> f) { try { return f(); } catch (Exception e) { return "(couldn't read: " + e.GetType().Name + ": " + e.Message + ")"; } }

        // Wine's version when the game runs under Wine or Proton (ntdll's wine_get_version), else null
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr WineVersion();
        internal static string Wine()
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
