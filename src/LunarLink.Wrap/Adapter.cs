// The hot object for one wrapped copy: what the core starts and stops, like any hot plugin's.
//   Start: the plugin created and its Load() run, as BepInEx would (the core does that once BepInEx has loaded every
//          normal plugin, or at once when it's turned on while the game runs).
//   Stop:  its own Unload() if it has one; the threads, timers, native hooks and event handlers it started (HotTrack);
//          the Unity components of its Il2Cpp types, and the game objects that held only those (not BepInEx's); its log
//          source. The core then takes out its Harmony patches and routes and drops the copy; once Unity has removed
//          the objects, HotTrack lets go of them and frees the bridges Il2CppInterop pinned for its types.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace LunarLink
{
    public sealed class Adapter : IWrapped
    {
        readonly Type type;
        readonly int track;
        BasePlugin bep;
        readonly List<string> left = new List<string>();
        IEnumerable<string> IWrapped.Leftovers => left;

        internal Adapter(Type type, int track) { this.type = type; this.track = track; }

        public void Start(string state)
        {
            bep = (BasePlugin)Activator.CreateInstance(type);
            bep.Load();
        }

        public string Stop()
        {
            left.Clear();
            if (bep == null) { HotTrack.Discard(track); return null; }
            var un = type.GetMethod("Unload", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (un != null && un.DeclaringType != typeof(BasePlugin))
            {
                try { if (!bep.Unload()) left.Add("its Unload() said it couldn't unload everything"); }
                catch (Exception e) { left.Add("its Unload() threw: " + e.Message); }
            }
            left.AddRange(HotTrack.Stop(track));
            try { DestroyObjects(); } catch (Exception e) { left.Add("removing its components: " + e.Message); }
            try { BepInEx.Logging.Logger.Sources.Remove(bep.Log); } catch { }
            HotTrack.Forget(type.Assembly);
            bep = null;
            return null;
        }

        // passed on to the plugin, when it has them
        public string HandleHttp(string method, string path, NameValueCollection query, string body) =>
            Call("HandleHttp", new[] { typeof(string), typeof(string), typeof(NameValueCollection), typeof(string) }, method, path, query, body) as string;
        public string[] HttpHelp() => Call("HttpHelp", Type.EmptyTypes) as string[];
        public string UpdateFeed() => Call("UpdateFeed", Type.EmptyTypes) as string;

        object Call(string name, Type[] args, params object[] values)
        {
            if (bep == null) return null;
            var m = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, args, null);
            return m == null ? null : m.Invoke(bep, values);
        }

        // Every component of the copy's Il2Cpp types, hidden ones too (BasePlugin.AddComponent puts them on BepInEx's
        // hidden manager object). Destroy, not DestroyImmediate: this runs inside LunarLink's own Update, on that same
        // manager object, and tearing a neighbour out mid-Update kills the game. Types are told apart by class pointer:
        // Il2Cpp reflection on an injected type (Il2CppSystem.Type.FullName) crashes the game.
        void DestroyObjects()
        {
            var asm = type.Assembly;
            var types = new List<(string name, Il2CppSystem.Type il2, IntPtr cls)>();
            foreach (var t in Types(asm).Where(t => typeof(Component).IsAssignableFrom(t)))
                try
                {
                    var cls = Il2CppClassPointerStore.GetNativeClassPointer(t);
                    if (cls != IntPtr.Zero) types.Add((t.FullName, Il2CppType.From(t), cls));
                }
                catch { }
            if (types.Count == 0) return;
            var classes = new HashSet<IntPtr>(types.Select(t => t.cls));
            var held = new List<nint>();
            int comps = 0, objects = 0;
            foreach (var (_, it, _) in types)
                foreach (var o in Resources.FindObjectsOfTypeAll(it))
                {
                    var c = o.TryCast<Component>(); if (c == null) continue;
                    var go = c.gameObject;
                    bool onlyMine = !go.name.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase)
                        && go.GetComponents<Component>().All(x => x.TryCast<Transform>() != null || classes.Contains(IL2CPP.il2cpp_object_get_class(x.Pointer)));
                    held.Add(IL2CPP.il2cpp_gchandle_new(c.Pointer, false));
                    if (onlyMine) { UnityEngine.Object.Destroy(go); objects++; } else { UnityEngine.Object.Destroy(c); comps++; }
                }
            Pump.Journal("hot: destroying " + comps + " component(s) and " + objects + " game object(s) of " + string.Join(", ", types.Select(t => t.name)));
            if (HotTrack.PinProblem != null) left.Add("its Il2Cpp types' bridges can't be freed (" + HotTrack.PinProblem + "): this copy can't leave memory");
            HotTrack.ReleaseWhenGone(asm, types.Select(t => t.il2).ToList(), held);
        }

        internal static IEnumerable<Type> Types(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }
    }
}
