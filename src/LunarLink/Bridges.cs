// Frees the game-side bridges of removed Harmony patches, so an unloaded hot plugin's code can be collected.
// Il2CppInterop builds a native-to-managed bridge for every Harmony change to a game method (each patch AND each
// unpatch) and keeps it in a static list, Il2CppDetourMethodPatcher.DelegateCache, for ever. Each bridge points at the
// generated method that calls the plugin's patch methods, so every copy of a reloaded plugin stayed in memory.
// Only one bridge per patched method is live: the one its patcher's current native detour calls. Live bridges are
// found by native address (the detour's public Detour pointer is the bridge's function pointer), which doesn't depend
// on how BepInEx wraps its detours. The rest were
// detached when the method was re-patched. A bridge is dropped only if it was already dead at the previous purge,
// so nothing is freed in the same reload that detached it.
// Harmony's DelegateTypeFactory.TypeCache holds each bridge's generated method too (it's keyed by it), so a freed
// bridge's entry is dropped there as well. That cache only speeds up making new bridges. Entries are matched by the
// bridge's delegate type, which is made new for every bridge (the key isn't the same object as the delegate's Method).
// Measured 2026-11-13: these two caches were the only things holding old hot-plugin copies.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib.Public.Patching;

namespace LunarLink
{
    static class Bridges
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;
        static HashSet<object> deadBefore = new HashSet<object>(ReferenceEqualityComparer.Instance);
        static int freed, typesFreed; static string problem;

        internal static void Purge()
        {
            problem = null;
            try
            {
                var patcherType = Type.GetType("Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher, Il2CppInterop.HarmonySupport", true);
                var cache = (List<object>)patcherType.GetField("DelegateCache", Any | BindingFlags.Static).GetValue(null);
                var patchers = (Dictionary<MethodBase, MethodPatcher>)typeof(PatchManager).GetField("MethodPatchers", Any | BindingFlags.Static).GetValue(null);
                var nativeDetourF = patcherType.GetField("nativeDetour", Any | BindingFlags.Instance);
                var livePtrs = new HashSet<IntPtr>();
                lock (patchers)
                    foreach (var mp in patchers.Values)
                    {
                        if (mp == null || !patcherType.IsInstanceOfType(mp)) continue;
                        var nd = nativeDetourF.GetValue(mp);
                        if (nd == null) continue;
                        var ptr = nd.GetType().GetProperty("Detour", Any | BindingFlags.Instance)?.GetValue(nd);
                        if (ptr == null || (IntPtr)ptr == IntPtr.Zero) { problem = "can't read the live bridge of " + nd.GetType().FullName + "; nothing freed"; return; }
                        livePtrs.Add((IntPtr)ptr);
                    }
                var dead = new HashSet<object>(ReferenceEqualityComparer.Instance);
                int live = 0;
                foreach (var d in cache)
                {
                    if (d is Delegate del && livePtrs.Contains(Marshal.GetFunctionPointerForDelegate(del))) live++;
                    else dead.Add(d);
                }
                if (live != livePtrs.Count) { problem = "matched " + live + " of " + livePtrs.Count + " live bridges; nothing freed"; return; }
                var gone = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var d in cache) if (deadBefore.Contains(d) && dead.Contains(d)) gone.Add(d.GetType());
                int n = cache.RemoveAll(d => deadBefore.Contains(d) && dead.Contains(d));
                freed += n;
                var typeCache = (System.Collections.IDictionary)typeof(HarmonyLib.DelegateTypeFactory).GetField("TypeCache", Any | BindingFlags.Static)?.GetValue(null);
                if (typeCache != null && gone.Count > 0)
                    lock (typeCache)
                    {
                        var keys = new List<object>();
                        foreach (System.Collections.DictionaryEntry e in typeCache)
                            foreach (var entry in (System.Collections.IEnumerable)e.Value)
                                if (gone.Contains(entry.GetType().GetField("delegateType")?.GetValue(entry))) { keys.Add(e.Key); break; }
                        foreach (var k in keys) typeCache.Remove(k);
                        typesFreed += keys.Count;
                        if (keys.Count < gone.Count) problem = "freed " + gone.Count + " bridges but matched only " + keys.Count + " type-cache entries";
                    }
                dead.ExceptWith(deadBefore);
                deadBefore = dead;
                if (n > 0) Pump.Journal("hot: freed " + n + " dead patch bridges (" + cache.Count + " kept, " + live + " live)");
            }
            catch (Exception e) { problem = e.GetType().Name + ": " + e.Message; Plugin.L.LogWarning("hot: bridge clean-up: " + problem); }
        }

        internal static string Status()
        {
            int kept = -1;
            try
            {
                var t = Type.GetType("Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher, Il2CppInterop.HarmonySupport");
                kept = ((List<object>)t?.GetField("DelegateCache", Any | BindingFlags.Static)?.GetValue(null))?.Count ?? -1;
            }
            catch { }
            return "{\"kept\":" + kept + ",\"freed\":" + freed + ",\"typesFreed\":" + typesFreed + ",\"waiting\":" + deadBefore.Count + ",\"problem\":" + J.S(problem) + "}";
        }
    }
}
