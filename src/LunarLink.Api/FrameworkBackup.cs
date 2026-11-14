using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace LunarLink
{
    // MoonFramework keeps which stand-in structures are really Quantum adapters (and their channels) in
    // MoonSessions/slot-N/moonframework.json, rewritten at every save from the world that is loaded. On 2026-11-07 it came
    // back with an empty list (likely written while the player was in a friend's world) and all 873 adapters loaded as
    // plain splitters. Once a world has loaded, every slot's file that still lists structures is copied to
    // BepInEx/plugins/LunarLink/framework-backups/slot-N/ (one copy per load, the last 20 kept) - a wiped file is then a
    // one-file restore.
    static class FrameworkBackup
    {
        public static string Last = "";
        public static void Run()
        {
            try
            {
                var sessions = Path.Combine(Application.persistentDataPath, "MoonSessions");
                if (!Directory.Exists(sessions)) { Last = "no MoonSessions folder"; return; }
                var root = Path.Combine(Api.DataDir, "framework-backups");
                int n = 0;
                foreach (var slot in Directory.GetDirectories(sessions, "slot-*"))
                {
                    var f = Path.Combine(slot, "moonframework.json");
                    if (!File.Exists(f)) continue;
                    var text = File.ReadAllText(f);
                    if (!text.Contains("\"building\"")) { Pump.Journal("framework backup: " + Path.GetFileName(slot) + " lists no structures - not copied"); continue; }
                    var dir = Path.Combine(root, Path.GetFileName(slot)); Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "moonframework-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), text);
                    foreach (var old in Directory.GetFiles(dir, "moonframework-*.json").OrderByDescending(x => x).Skip(20)) File.Delete(old);
                    n++;
                }
                Last = n + " slot file(s) backed up"; Pump.Journal("framework backup: " + Last);
            }
            catch (Exception e) { Last = "failed: " + e.Message; Pump.Journal("framework backup failed: " + e.Message); }
        }
    }
}
