using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace LunarLink
{
    // Reading the logs: errors and warnings with the lines round them, a log's start and end, a freeze's dump from the
    // journal, and Unity's crash reports.
    static partial class Support
    {
        static readonly Regex BepLevel = new Regex(@"^\[(Error|Fatal|Warning)\s*:\s*([^\]]*?)\s*\]", RegexOptions.Compiled);
        static bool BepError(string l) => l.StartsWith("[Error") || l.StartsWith("[Fatal") || l.StartsWith("[Warning")
            || l.Contains("Exception:") || l.StartsWith("   at ");
        static bool GameError(string l) => l.Contains("Exception") || l.Contains("Crash!!!") || l.StartsWith("ERROR")
            || l.Contains("Error:") || l.Contains("Fatal");

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
                foreach (var kv in bySource.OrderByDescending(k => k.Value)) problems.Add(kv.Value + " error"
                    + (kv.Value == 1 ? "" : "s") + " from " + kv.Key + " in the " + what);
                foreach (var kv in warnBy.OrderByDescending(k => k.Value)) warnings.Add(kv.Value + " warning"
                    + (kv.Value == 1 ? "" : "s") + " from " + kv.Key + " in the " + what);
                if (what.StartsWith("game")) { int ex = lines.Count(l => l.Contains("Exception"));
                    if (ex > 0) problems.Add(ex + " exception line" + (ex == 1 ? "" : "s") + " in the " + what); }
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
            problems.Add("The game froze " + dumps + " time" + (dumps == 1 ? "" : "s") + " in " + when
                + " (3 s or more without a frame): see " + (when == "this run" ? "logs" : "previous-run")
                + "/lunarlink-stuck.txt");
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
                add("crashes/list.txt",
                    string.Join("\n", all.Select(d => Path.GetFileName(d) + "   "
                    + Directory.GetLastWriteTime(d).ToString("yyyy-MM-dd HH:mm:ss"))));
                foreach (var d in all.Take(3))
                {
                    var e = Path.Combine(d, "error.log");
                    if (File.Exists(e)) add("crashes/" + Path.GetFileName(d) + "-error.log", Tail(ReadLines(e), 400));
                }
                var newest = Directory.GetLastWriteTime(all[0]);
                if (DateTime.Now - newest < TimeSpan.FromDays(2)) problems.Add("Unity wrote a crash report at "
                    + newest.ToString("yyyy-MM-dd HH:mm") + ": see crashes/");
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
        static string Head(List<string> l, int n) => l == null ? null
            : string.Join("\n", l.Take(n)) + (l.Count > n ? "\n... (" + (l.Count - n) + " more lines)" : "");
        static string Tail(List<string> l, int n) => l == null ? null
            : (l.Count > n ? "... (" + (l.Count - n) + " earlier lines)\n" : "")
            + string.Join("\n", l.Skip(Math.Max(0, l.Count - n)));

        // ---- blanking ----
    }
}
