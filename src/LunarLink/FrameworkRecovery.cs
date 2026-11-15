using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using UnityEngine;

namespace LunarLink
{
    // MoonFramework keeps one moonframework.json per save slot. A save made while the loaded
    // world is not the selected slot's world can replace that file with an empty one. LunarLink
    // keeps rolling known-good copies and repairs an empty/corrupt file before MoonFramework loads.
    static class FrameworkRecovery
    {
        internal static string Run(bool enabled)
        {
            if (!enabled) return "disabled";

            try
            {
                var sessions = Path.Combine(Application.persistentDataPath, "MoonSessions");
                var backups = Path.Combine(Paths.PluginPath, "LunarLink", "framework-backups");
                if (!Directory.Exists(sessions)) return "no MoonSessions folder";
                Directory.CreateDirectory(backups);

                int repaired = 0, copied = 0;
                foreach (var slotDir in Directory.GetDirectories(sessions, "slot-*"))
                {
                    var slot = Path.GetFileName(slotDir);
                    var current = Path.Combine(slotDir, "moonframework.json");
                    var backupDir = Path.Combine(backups, slot);
                    var latest = LatestValidBackup(backupDir);

                    if (latest != null && NeedsRepair(current) && LooksPlayed(slotDir)
                        && File.GetLastWriteTimeUtc(current) >= File.GetLastWriteTimeUtc(latest))
                    {
                        var preserved = current + ".before-lunarlink-recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        if (File.Exists(preserved)) preserved += "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                        File.Copy(current, preserved);

                        var temp = current + ".lunarlink-recovery";
                        File.Copy(latest, temp, true);
                        File.Move(temp, current, true);
                        repaired++;
                        Plugin.L.LogWarning("MoonFramework recovery: restored " + slot + " from " + Path.GetFileName(latest)
                            + "; original saved as " + Path.GetFileName(preserved));
                    }

                    if (!HasStructures(current)) continue;
                    Directory.CreateDirectory(backupDir);
                    var backup = Path.Combine(backupDir, "moonframework-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    File.Copy(current, backup, true);
                    foreach (var old in Directory.GetFiles(backupDir, "moonframework-*.json")
                        .OrderByDescending(x => Path.GetFileName(x)).Skip(20)) File.Delete(old);
                    copied++;
                }

                return repaired + " save slot(s) restored; " + copied + " valid slot file(s) backed up";
            }
            catch (Exception e)
            {
                Plugin.L.LogError("MoonFramework recovery failed: " + e.Message);
                return "failed: " + e.Message;
            }
        }

        static bool NeedsRepair(string file) => File.Exists(file) && !HasStructures(file);

        static bool HasStructures(string file)
        {
            if (!File.Exists(file)) return false;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("structures", out var structures)
                    || structures.ValueKind != JsonValueKind.Array || structures.GetArrayLength() == 0) return false;
                return structures.EnumerateArray().Any(s => s.TryGetProperty("building", out var building)
                    && building.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(building.GetString()));
            }
            catch { return false; }
        }

        static string LatestValidBackup(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "moonframework-*.json")
                .OrderByDescending(x => Path.GetFileName(x)).FirstOrDefault(HasStructures);
        }

        // Do not touch a genuinely new/unused slot. A played slot has the game's own activity marker.
        static bool LooksPlayed(string slotDir)
        {
            try
            {
                var file = Path.Combine(slotDir, "slot.json");
                if (!File.Exists(file)) return false;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                return Number(root, "playSeconds") > 0 || Number(root, "regions") > 0 || Number(root, "dollars") > 0;
            }
            catch { return false; }
        }

        static double Number(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) ? number : 0;
    }
}
