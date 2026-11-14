// What a wrapped plugin starts that BepInEx never takes back, recorded so turning it off can undo it (see Wrap.cs).
// The wrapper rewrites the plugin's DLL as it loads it, so these calls come here with the copy's id:
//   new Thread(...)           -> NewThread: the thread runs inside a guard, so off can interrupt it and let it end
//   new Timer(...)            -> Keep: disposed when off
//   native detours (INativeDetour, MonoMod's Detour/Hook/NativeDetour...) -> Keep: disposed (undone) when off
//   x.add_Event(handler)      -> Event: removed again when off
//   an AssetBundle handed back -> Keep: unloaded when off (Unity loads a bundle once: the next copy needs it gone)
// Off, in this order: threads stopped first (one may be inside a detour), then detours and timers, then events.
// Pins: Il2CppInterop pins a bridge per method of every type it registers for Il2Cpp and never frees them. While a
// wrapped plugin registers a type those pins are recorded here, and freed once its objects are gone.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace LunarLink
{
    public static class HotTrack
    {
        sealed class Bag
        {
            public readonly List<object> Keep = new List<object>();
            public readonly List<Thread> Threads = new List<Thread>();
            public readonly List<(object target, object handler, MethodBase remove)> Events = new List<(object, object, MethodBase)>();
            public volatile bool Stopping;
        }

        static readonly ConcurrentDictionary<int, Bag> bags = new ConcurrentDictionary<int, Bag>();
        static int next;

        internal static int New() { int id = Interlocked.Increment(ref next); bags[id] = new Bag(); return id; }
        internal static void Discard(int id) => bags.TryRemove(id, out _);

        static Bag Of(int id) => bags.TryGetValue(id, out var b) ? b : null;

        // ---- called from a wrapped plugin's rewritten code ----

        // assembly.Location: a copy loaded from memory has none, so a wrapped copy answers with its DLL's path
        static readonly ConcurrentDictionary<Assembly, string> locations = new ConcurrentDictionary<Assembly, string>();
        // a wrapped copy: answers for Location, and its Il2Cpp types' pins are recorded
        internal static void Watch(Assembly a, string file) { locations[a] = file; }
        internal static void Forget(Assembly a) => locations.TryRemove(a, out _);
        public static string Location(Assembly a) => a != null && locations.TryGetValue(a, out var f) ? f : a?.Location;

        public static Thread NewThread(ThreadStart start, int id) => Guard(id, () => start());
        public static Thread NewThread(ThreadStart start, int maxStack, int id) => Guard(id, () => start(), maxStack);
        public static Thread NewThread(ParameterizedThreadStart start, int id) => GuardP(id, start, 0);
        public static Thread NewThread(ParameterizedThreadStart start, int maxStack, int id) => GuardP(id, start, maxStack);

        public static void Keep(object o, int id)
        {
            var b = Of(id); if (b == null || o == null) return;
            lock (b) b.Keep.Add(o);
        }

        public static void Event(object target, object handler, RuntimeMethodHandle remove, RuntimeTypeHandle type, int id)
        {
            var b = Of(id); if (b == null || handler == null) return;
            MethodBase m; try { m = MethodBase.GetMethodFromHandle(remove, type); } catch { return; }
            lock (b) b.Events.Add((target, handler, m));
        }

        static Thread Guard(int id, Action body, int maxStack = 0)
        {
            ThreadStart run = () => Run(id, body);
            var t = maxStack > 0 ? new Thread(run, maxStack) : new Thread(run);
            var b = Of(id); if (b != null) lock (b) b.Threads.Add(t);
            return t;
        }

        static Thread GuardP(int id, ParameterizedThreadStart body, int maxStack)
        {
            ParameterizedThreadStart run = new ParameterizedThreadStart((object o) => Run(id, () => body(o)));
            var t = maxStack > 0 ? new Thread(run, maxStack) : new Thread(run);
            var b = Of(id); if (b != null) lock (b) b.Threads.Add(t);
            return t;
        }

        static void Run(int id, Action body)
        {
            try { body(); }
            catch (ThreadInterruptedException) when (Of(id)?.Stopping ?? true) { }
            finally
            {
                // a thread that attached itself to Il2Cpp and ends without detaching would trip up the game's GC
                try { var me = IL2CPP.il2cpp_thread_current(); if (me != IntPtr.Zero) IL2CPP.il2cpp_thread_detach(me); } catch { }
            }
        }

        // ---- off ----

        // Undo everything copy id started. Returns what couldn't be undone.
        internal static List<string> Stop(int id)
        {
            var left = new List<string>();
            if (!bags.TryRemove(id, out var b)) return left;
            b.Stopping = true;
            List<Thread> threads; List<object> keep; List<(object target, object handler, MethodBase remove)> events;
            lock (b) { threads = b.Threads.ToList(); keep = b.Keep.ToList(); events = b.Events.ToList(); }
            var live = threads.Where(t => t.IsAlive).ToList();
            foreach (var t in live) try { t.Interrupt(); } catch { }
            var deadline = DateTime.UtcNow.AddSeconds(2);
            foreach (var t in live) { var ms = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds); try { t.Join(ms); } catch { } }
            var stuck = live.Where(t => t.IsAlive).Select(t => t.Name ?? "a thread").ToList();
            if (stuck.Count > 0) left.Add(stuck.Count + " thread(s) didn't stop (" + string.Join(", ", stuck.Take(3)) + "): busy without ever waiting");
            int undone = 0, bundles = 0;
            foreach (var o in Enumerable.Reverse(keep))
            {
                if (o.GetType().FullName == "UnityEngine.AssetBundle")
                {
                    // Unload(true): the bundle and what was loaded from it; one already unloaded (by the plugin) throws, fine
                    try { o.GetType().GetMethod("Unload", new[] { typeof(bool) })?.Invoke(o, new object[] { true }); bundles++; } catch { }
                    continue;
                }
                try
                {
                    if (o is IDisposable d) d.Dispose();
                    else o.GetType().GetMethod("Undo", Type.EmptyTypes)?.Invoke(o, null);
                    undone++;
                }
                catch (Exception e) { left.Add("undoing " + o.GetType().Name + ": " + (e.InnerException ?? e).Message); }
            }
            int removed = 0;
            foreach (var (target, handler, remove) in Enumerable.Reverse(events))
            {
                try { remove.Invoke(remove.IsStatic ? null : target, new[] { handler }); removed++; }
                catch (Exception e) { left.Add("removing its " + remove.Name.Substring(7) + " handler: " + (e.InnerException ?? e).Message); }
            }
            Pump.Journal("hot: stopped " + (live.Count - stuck.Count) + " thread(s), undid " + undone + " hook(s)/timer(s), unloaded " + bundles + " asset bundle(s), removed " + removed + " event handler(s)");
            return left;
        }

        // ---- pins ----

        [ThreadStatic] static Assembly registering;                       // the assembly whose type is being registered now
        static readonly Dictionary<Assembly, List<GCHandle>> pins = new Dictionary<Assembly, List<GCHandle>>();
        static string pinProblem;
        internal static string PinProblem => pinProblem;

        // Patched in once, at game start: ClassInjector's trampolines and constructors get their handles from Alloc.
        internal static void InstallPins(Harmony h)
        {
            try
            {
                var ci = typeof(Il2CppInterop.Runtime.Injection.ClassInjector);
                var reg = ci.GetMethod("RegisterTypeInIl2Cpp", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(Type), typeof(Il2CppInterop.Runtime.Injection.RegisterTypeOptions) }, null);
                var targets = new[] { "CreateTrampoline", "CreateEmptyCtor" }.Select(n => ci.GetMethod(n, BindingFlags.NonPublic | BindingFlags.Static)).ToList();
                if (reg == null || targets.Any(t => t == null)) { pinProblem = "Il2CppInterop changed: in-game types of wrapped plugins can't be freed"; return; }
                h.Patch(reg, prefix: new HarmonyMethod(typeof(HotTrack), nameof(RegPrefix)), finalizer: new HarmonyMethod(typeof(HotTrack), nameof(RegFinalizer)));
                foreach (var t in targets) h.Patch(t, transpiler: new HarmonyMethod(typeof(HotTrack), nameof(AllocTranspiler)));
            }
            catch (Exception e) { pinProblem = "pins: " + e.Message; LunarLink.Plugin.L.LogWarning("hot: " + pinProblem); }
        }

        static void RegPrefix(Type type, out Assembly __state) { __state = registering; registering = type?.Assembly; }
        static Exception RegFinalizer(Exception __exception, Assembly __state) { registering = __state; return __exception; }

        static readonly MethodInfo gcAlloc = typeof(GCHandle).GetMethod(nameof(GCHandle.Alloc), new[] { typeof(object) });
        static IEnumerable<CodeInstruction> AllocTranspiler(IEnumerable<CodeInstruction> code)
        {
            foreach (var i in code)
            {
                if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo m && m == gcAlloc)
                    i.operand = typeof(HotTrack).GetMethod(nameof(Alloc), BindingFlags.Public | BindingFlags.Static);
                yield return i;
            }
        }

        public static GCHandle Alloc(object o)
        {
            var h = GCHandle.Alloc(o);
            var a = registering;
            if (a != null && locations.ContainsKey(a)) lock (pins) { if (!pins.TryGetValue(a, out var l)) pins[a] = l = new List<GCHandle>(); l.Add(h); }
            return h;
        }

        static int FreePins(Assembly a)
        {
            List<GCHandle> l;
            lock (pins) { if (!pins.TryGetValue(a, out l)) return 0; pins.Remove(a); }
            foreach (var h in l) try { h.Free(); } catch { }
            return l.Count;
        }

        // ---- after a wrapped copy's Unity objects are destroyed ----

        // Unity removes destroyed objects at the end of the frame. held: an Il2Cpp GC handle on each destroyed component,
        // so it can't be finalized before Frame lets go of it.
        static readonly List<(Assembly asm, List<Il2CppSystem.Type> types, List<nint> held)> waiting = new List<(Assembly, List<Il2CppSystem.Type>, List<nint>)>();

        internal static void ReleaseWhenGone(Assembly asm, List<Il2CppSystem.Type> types, List<nint> held)
        {
            lock (waiting) waiting.Add((asm, types, held));
        }

        // Each frame (Hot.Update): for each copy whose objects are all gone, let go of them and free its types' bridges.
        // A destroyed component's Il2Cpp object lives on until the game's GC finalizes it, holding a handle to its .NET
        // object (and so to the whole copy). That handle is pointed at a placeholder and the real one freed: the copy is
        // let go now, and the game's finalizer later frees the placeholder's handle instead. Only then are the bridges
        // freed, since nothing of the types can run any more.
        internal static void Frame()
        {
            if (waiting.Count == 0) return;
            lock (waiting)
                for (int i = waiting.Count - 1; i >= 0; i--)
                {
                    var (asm, types, held) = waiting[i];
                    if (types.Any(t => UnityEngine.Resources.FindObjectsOfTypeAll(t).Length > 0)) continue;
                    waiting.RemoveAt(i);
                    int swapped = 0;
                    foreach (var h in held)
                    {
                        var ptr = IL2CPP.il2cpp_gchandle_get_target(h);
                        if (ptr != IntPtr.Zero)
                            try
                            {
                                var old = GCHandle.FromIntPtr(Il2CppInterop.Runtime.Runtime.ClassInjectorBase.GetGcHandlePtrFromIl2CppObject(ptr));
                                Il2CppInterop.Runtime.Injection.ClassInjector.AssignGcHandle(ptr, GCHandle.Alloc(new object()));
                                old.Free(); swapped++;
                            }
                            catch (Exception e) { Pump.Journal("hot: couldn't let go of a destroyed object: " + e.Message); }
                        IL2CPP.il2cpp_gchandle_free(h);
                    }
                    Pump.Journal("hot: " + asm.GetName().Name + "'s objects are gone: let go of " + swapped + ", freed " + FreePins(asm) + " Il2Cpp bridge(s)");
                }
        }
    }
}
