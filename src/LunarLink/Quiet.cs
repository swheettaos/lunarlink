// Errors ignored on purpose: a guard around a game call that may not be there yet (no world, no lobby), or a best-effort
// cleanup that mustn't stop the rest. Each place still says when it happens: the first time in a session it's logged
// (where, and the error), after that only counted, so a guard in a per-frame path can't flood the log. The support file
// lists every place with its count.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace LunarLink
{
    static class Quiet
    {
        static readonly Dictionary<string, int> seen = new Dictionary<string, int>();     // locked: the server's threads note too

        // in a catch: Quiet.Note(caught) (the file and line come from the compiler)
        internal static void Note(Exception e, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            var at = System.IO.Path.GetFileName(file) + ":" + line;
            int n;
            lock (seen) { seen.TryGetValue(at, out n); seen[at] = ++n; }
            if (n == 1) Plugin.L?.LogInfo("ignored at " + at + ": " + e.GetType().Name + ": " + e.Message + " (only counted from now on)");
        }

        // every place that ignored an error, with how many times, most first
        internal static List<(string at,
            int count)> Counts() { lock (seen) return seen.Select(kv => (kv.Key,
            kv.Value)).OrderByDescending(x => x.Item2).ToList(); }
    }
}
