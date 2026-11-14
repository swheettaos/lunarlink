// What the core keeps of LunarLink's updates; the rest (checking GitHub, downloading, installing, reloading, the
// /update endpoints) is the LunarLink.Update module, so it updates like any module.
//   Finish()       at game start, before any hot plugin loads: a core update installed last run left the new modules
//                  in plugins/LunarLink/update/, waiting for the new core they were built for; they move into
//                  BepInEx/hot/ now, the old core file goes, and JustFinished tells the Update module to check once
//                  more when it starts.
//   Status, Check, Apply   the slot the F9 window reaches the Update module through: its Start fills them, its Stop
//                  empties them (so no old copy is kept alive), and while it's off they're null.
using System;
using System.IO;

namespace LunarLink
{
    static class Updates
    {
        internal static bool JustFinished;
        internal static Func<string> Status;            // GET /update's answer
        internal static Func<string> Check;             // POST /update/check
        internal static Func<string, string> Apply;     // POST /update/apply: "" the latest release, else ?module= (a name or "all")

        internal static void Finish()
        {
            var dir = Api.DataDir;
            try { if (File.Exists(Path.Combine(dir, "LunarLink.dll.old"))) File.Delete(Path.Combine(dir, "LunarLink.dll.old")); } catch { }
            var staged = Path.Combine(dir, "update");
            if (!Directory.Exists(staged)) return;
            try
            {
                Directory.CreateDirectory(Hot.Dir);
                foreach (var f in Directory.GetFiles(staged, "LunarLink.*.dll")) File.Move(f, Path.Combine(Hot.Dir, Path.GetFileName(f)), true);
                Directory.Delete(staged, true);
                JustFinished = true;
                Plugin.L.LogInfo("update: the new modules are in place");
            }
            catch (Exception e) { Plugin.L.LogError("update: couldn't move the new modules into BepInEx/hot/: " + e.Message); }
        }
    }
}
