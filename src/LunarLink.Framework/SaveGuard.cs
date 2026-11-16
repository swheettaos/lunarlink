using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    // MoonFramework keeps one moonframework.json per save slot: which of the game's structures are its buildings (Quantum
    // adapters and the like) and the mods' data. Lose it and those buildings load as their plain stand-ins (splitters,
    // mergers) with their channels gone.
    //
    // How it got lost (MoonFramework 0.7.8, SaveData.Write): a write goes to the slot that's active *now*, with whatever
    // MoonFramework holds in memory - even when that isn't the slot the world came from (it only warns), and after the
    // world was unloaded (nothing loaded: an empty file). Leaving a friend's game left exactly that: an empty file over
    // the player's own slot (2026-11-13).
    //
    // So: (1) such a write is refused. (2) Every slot's file is copied whenever it changes (the last 20 different
    // versions, BepInEx/plugins/LunarLink/framework-backups/slot-N/). (3) As a world loads - just before MoonFramework
    // reads the file - a slot whose file is unreadable or has no structures, while the world being loaded still has
    // structures that the newest copy says were MoonFramework's (same id, place and stand-in kind), gets that copy back;
    // the damaged file is kept beside it. A file that's empty because those buildings really were removed is left alone.
    // MoonFramework is reached by name only: LunarLink builds and runs without it.
    static class SaveGuard
    {
        internal static bool Active, On = true;
        internal static int Blocked, Restored, Copied;
        internal static string Reason;
        static Type saveData; static FieldInfo loadedSlotF;
        static Harmony harmony;
        static readonly HashSet<IntPtr> checkedLoads = new();
        static readonly HashSet<string> warned = new(StringComparer.Ordinal);
        static float nextLook;
        static readonly Dictionary<int, DateTime> seen = new();

        internal static string Folder => Path.Combine(Api.DataDir, "framework-backups");
        const int Keep = 20;

        // MoonFramework's assembly, if it's loaded
        static Assembly Framework() => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "MoonFramework");

        internal static bool Install()
        {
            if (Active) return true;
            var fw = Framework();
            if (fw == null) return false;
            try
            {
                saveData = fw.GetType("MoonFramework.Internal.SaveData", true);
                loadedSlotF = saveData.GetField("currentSlot", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    ?? throw new Exception("no SaveData.currentSlot");
                var write = saveData.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null)
                    ?? throw new Exception("no SaveData.Write()");
                var restore = typeof(MoonWorld).GetMethod("RestoreDepots", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new Exception("no MoonWorld.RestoreDepots");
                harmony = new Harmony(FrameworkModule.Id);
                harmony.Patch(write, prefix: new HarmonyMethod(typeof(SaveGuard).GetMethod(nameof(WritePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                // before MoonFramework's own prefix on RestoreDepots, which reads the file
                harmony.Patch(restore, prefix: new HarmonyMethod(typeof(SaveGuard).GetMethod(nameof(LoadPrefix), BindingFlags.Static | BindingFlags.NonPublic))
                    { priority = Priority.First });
                Active = true;
                CopyAll();
                Plugin.L.LogInfo("MoonFramework found: its save files are guarded (" + Copied + " slot file(s) copied)");
                return true;
            }
            catch (Exception e)
            {
                Reason = e.Message; harmony?.UnpatchSelf(); harmony = null;
                Plugin.L.LogWarning("MoonFramework found, but its saves can't be guarded: " + e.Message);
                return false;
            }
        }

        internal static void Uninstall() { harmony?.UnpatchSelf(); harmony = null; Active = false; }

        static string SlotFile(int slot)
        {
            try { var d = MoonSaveSlots.SlotFolder(slot); return string.IsNullOrEmpty(d) ? null : Path.Combine(d, "moonframework.json"); }
            catch { return null; }
        }

        static string SessionsFolder()
        {
            var f = SlotFile(MoonSaveSlots.Active);
            return f == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(f));
        }

        // ------------------------------------------------ (1) the write into the wrong slot

        static bool WritePrefix()
        {
            if (!On) return true;
            int active, loaded;
            try { active = MoonSaveSlots.Active; loaded = (int)loadedSlotF.GetValue(null); }
            catch (Exception caught) { Quiet.Note(caught); return true; }
            if (loaded >= 0 && loaded == active) return true;
            Blocked++;
            string why = loaded < 0 ? "no world loaded" : "the world in memory came from slot " + loaded;
            if (warned.Add(active + ":" + loaded))
                Plugin.L.LogWarning("MoonFramework save guard: kept MoonFramework from writing slot " + active + "'s file (" + why + ")");
            return false;
        }

        // ------------------------------------------------ (2) copies, when a file changes

        internal static void Update(float now)
        {
            if (!Active || now < nextLook) return;
            nextLook = now + 2f;
            CopySlot(MoonSaveSlots.Active);
        }

        static void CopyAll()
        {
            var sessions = SessionsFolder();
            if (sessions == null || !Directory.Exists(sessions)) return;
            foreach (var d in Directory.GetDirectories(sessions, "slot-*"))
                if (int.TryParse(Path.GetFileName(d).Substring(5), out var slot)) CopySlot(slot);
        }

        static void CopySlot(int slot)
        {
            var file = SlotFile(slot);
            if (file == null || !File.Exists(file)) return;
            var t = File.GetLastWriteTimeUtc(file);
            if (seen.TryGetValue(slot, out var s) && s == t) return;
            seen[slot] = t;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); } catch (Exception caught) { Quiet.Note(caught); return; }
            if (Structures(bytes) == null) return;                         // an unreadable file isn't kept (an empty one is: mods' data)
            var dir = Path.Combine(Folder, "slot-" + slot);
            Directory.CreateDirectory(dir);
            var newest = Copies(dir).FirstOrDefault();
            if (newest != null && Hash(File.ReadAllBytes(newest)) == Hash(bytes)) return;   // same as the last copy
            File.WriteAllBytes(Path.Combine(dir, "moonframework-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), bytes);
            foreach (var old in Copies(dir).Skip(Keep)) File.Delete(old);
            Copied++;
        }

        // the newest copy that has buildings in it (a newer, empty one is skipped)
        static string NewestWithStructures(int slot) =>
            Copies(Path.Combine(Folder, "slot-" + slot)).FirstOrDefault(c => Structures(File.ReadAllBytes(c)) is { Count: > 0 });

        internal static IEnumerable<string> Copies(string dir) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "moonframework-*.json").OrderByDescending(Path.GetFileName) : Enumerable.Empty<string>();

        static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

        // the file's structures (id, x, z, savedAs), or null when it can't be read; an empty list when it has none
        internal static List<(int id, float x, float z, int savedAs)> Structures(byte[] bytes)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                var list = new List<(int, float, float, int)>();
                if (!doc.RootElement.TryGetProperty("structures", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
                foreach (var s in arr.EnumerateArray())
                    if (s.TryGetProperty("id", out var id) && s.TryGetProperty("x", out var x) && s.TryGetProperty("z", out var z)
                        && s.TryGetProperty("savedAs", out var sa))
                        list.Add((id.GetInt32(), x.GetSingle(), z.GetSingle(), sa.GetInt32()));
                return list;
            }
            catch { return null; }
        }

        // ------------------------------------------------ (3) a damaged file, put right as the world loads

        static void LoadPrefix(MoonLooseDustSnapshot snapshot)
        {
            if (!On || snapshot == null || !checkedLoads.Add(snapshot.Pointer)) return;
            try { RepairIfWiped(MoonSaveSlots.Active, snapshot); }
            catch (Exception e) { Plugin.L.LogWarning("MoonFramework save guard: check failed: " + e.Message); }
        }

        static void RepairIfWiped(int slot, MoonLooseDustSnapshot snapshot)
        {
            var file = SlotFile(slot);
            if (file == null || !File.Exists(file)) return;
            var now = Structures(File.ReadAllBytes(file));
            if (now != null && now.Count > 0) return;                       // has its structures: fine
            var copy = NewestWithStructures(slot);
            if (copy == null) return;
            var had = Structures(File.ReadAllBytes(copy));
            // still in the world as their stand-ins? then the file was wiped, not emptied on purpose
            var depots = snapshot.depots; int still = 0;
            if (depots != null)
                for (int i = 0; i < depots.Count; i++)
                {
                    var d = depots.get_Item(i); if (d == null) continue;
                    if (had.Any(h => h.id == d.id && h.savedAs == d.kind && Math.Abs(h.x - d.x) < 0.5f && Math.Abs(h.z - d.z) < 0.5f)) still++;
                }
            if (still == 0) return;
            var kept = file + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(file, kept, true);
            File.Copy(copy, file + ".lunarlink", true);
            File.Move(file + ".lunarlink", file, true);
            seen.Remove(slot);
            Restored++;
            Plugin.L.LogWarning("MoonFramework save guard: slot " + slot + "'s file was " + (now == null ? "unreadable" : "empty")
                + " while " + still + " of its buildings are still in the world; put back " + Path.GetFileName(copy)
                + " (the damaged file is kept as " + Path.GetFileName(kept) + ")");
        }

        // ------------------------------------------------ by hand (the endpoints)

        // a slot's copy put back; not for the slot whose world is loaded (MoonFramework would write over it)
        internal static string RestoreByHand(int slot, string name)
        {
            int loaded = -1; try { loaded = (int)loadedSlotF.GetValue(null); } catch (Exception caught) { Quiet.Note(caught); }
            if (Moon() && loaded == slot) return Api.Result(false, "slot " + slot + "'s world is loaded: go to the main menu first");
            var file = SlotFile(slot); if (file == null) return Api.Result(false, "no slot " + slot);
            var dir = Path.Combine(Folder, "slot-" + slot);
            var copy = name == null ? NewestWithStructures(slot) : Path.Combine(dir, Path.GetFileName(name));
            if (copy == null || !File.Exists(copy)) return Api.Result(false, "no such copy for slot " + slot);
            if (File.Exists(file)) File.Copy(file, file + ".replaced-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            File.Copy(copy, file + ".lunarlink", true); File.Move(file + ".lunarlink", file, true);
            seen.Remove(slot); Restored++;
            Plugin.L.LogInfo("MoonFramework save guard: slot " + slot + " put back from " + Path.GetFileName(copy) + " by request");
            return new JsonOut().Obj().Bool("ok", true).Num("slot", slot).Str("from", Path.GetFileName(copy));
        }

        static bool Moon() { try { return UnityEngine.Object.FindObjectOfType<MoonWorld>() != null; } catch { return false; } }

        internal static string Json()
        {
            var o = new JsonOut().Obj().Bool("ok", Reason == null).Bool("on", On).Bool("guarding", Active)
                .Num("writesBlocked", Blocked).Num("restored", Restored).Num("copiesMade", Copied).Str("reason", Reason).Arr("slots");
            var sessions = SessionsFolder();
            if (sessions != null && Directory.Exists(sessions))
                foreach (var d in Directory.GetDirectories(sessions, "slot-*").OrderBy(x => x))
                {
                    if (!int.TryParse(Path.GetFileName(d).Substring(5), out var slot)) continue;
                    var file = SlotFile(slot); var exists = file != null && File.Exists(file);
                    var st = exists ? Structures(File.ReadAllBytes(file)) : null;
                    o.Obj().Num("slot", slot).Str("file", !exists ? "none" : st == null ? "unreadable" : st.Count + " structures")
                        .Arr("copies");
                    foreach (var c in Copies(Path.Combine(Folder, "slot-" + slot)))
                        o.Obj().Str("name", Path.GetFileName(c)).Num("structures", Structures(File.ReadAllBytes(c))?.Count ?? -1).End();
                    o.End().End();
                }
            return o.End();
        }
    }
}
