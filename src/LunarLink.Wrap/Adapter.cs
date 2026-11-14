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
            Carry(state);                                   // the previous copy's choices, back in place
        }

        // A wrapped plugin can't hand over state itself, so its static enum settings - its modes and choices, such as
        // Mars Sweepers' PlanetContext.MenuPlanet (Moon / Mars) - are carried from one copy to the next: read when a copy
        // stops, written back once the next has loaded. Only enums: flags and counters ("set up already") start fresh,
        // so the new copy still sets itself up. As lines "Type|field|value".
        static string Choices(Assembly asm)
        {
            var lines = new List<string>();
            foreach (var t in HotTrack.Types(asm).Where(t => !t.ContainsGenericParameters))
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(f => f.FieldType.IsEnum && !f.IsLiteral && !f.IsInitOnly))
                    try { lines.Add(t.FullName + "|" + f.Name + "|" + Convert.ToInt64(f.GetValue(null))); } catch { }
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        void Carry(string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            var asm = type.Assembly; var carried = new List<string>();
            foreach (var line in state.Split('\n'))
            {
                var p = line.Split('|'); if (p.Length != 3 || !long.TryParse(p[2], out var v)) continue;
                try
                {
                    var f = asm.GetType(p[0], false)?.GetField(p[1], BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (f == null || !f.FieldType.IsEnum || f.IsInitOnly) continue;
                    var value = Enum.ToObject(f.FieldType, v);
                    if (!Equals(f.GetValue(null), value)) { f.SetValue(null, value); carried.Add(p[0].Substring(p[0].LastIndexOf('.') + 1) + "." + p[1].Replace("<", "").Replace(">k__BackingField", "") + " = " + value); }
                }
                catch { }
            }
            if (carried.Count > 0) Pump.Journal("hot: carried to the new copy: " + string.Join(", ", carried));
        }

        public string Stop()
        {
            left.Clear();
            if (bep == null) { HotTrack.Discard(track); return null; }
            string choices = null; try { choices = Choices(type.Assembly); } catch { }
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
            return choices;                                  // the next copy's state: its choices (Carry)
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

        // its Unity components and objects (HotTrack.DestroyObjects, shared with hot plugins)
        void DestroyObjects()
        {
            var (_, problem) = HotTrack.DestroyObjects(type.Assembly);
            if (problem != null) left.Add(problem);
        }

        internal static IEnumerable<Type> Types(Assembly asm) => HotTrack.Types(asm);
    }
}
