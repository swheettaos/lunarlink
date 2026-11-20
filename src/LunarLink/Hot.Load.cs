using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace LunarLink
{
    // A plugin's copy: loaded into its own context, started, called each frame and on world changes, stopped and unloaded
    // (its patches taken off even when its Stop forgets).
    static partial class Hot
    {
        // Once the world has loaded (the point LunarLink's own fixes go in): Start every loaded plugin.
        internal static void StartAll()
        {
            started = true;
            foreach (var p in plugins.Values) if (p.Obj != null && !p.Running && p.Error == null) Start(p, null);
        }

        internal static void Update() { HotTrack.Frame(); ReloadWaiting(); ReloadWatch.Check();
            foreach (var p in plugins.Values) if (p.Running && p.UpdateM != null) Frame(p, p.UpdateM); }
        internal static void LateUpdate() { foreach (var p in plugins.Values) if (p.Running && p.LateM != null) Frame(p, p.LateM); }

        static Plugin Get(string name)
        {
            if (!plugins.TryGetValue(name, out var p)) plugins[name] = p = new Plugin { Name = name };
            return p;
        }

        static void Load(Plugin p)
        {
            var file = FileOf(p.Name);
            if (p.Ctx != null) Unload(p, null);              // a copy that failed to load half-way is dropped first
            p.Error = null; p.Leftovers.Clear(); p.Warnings.Clear(); p.WrapNative = false; p.LoadsContent = false;
            var folder = Path.GetDirectoryName(file);
            p.Ctx = new Context("hot:" + p.Name + "#"
                + (p.Loads + 1), true,
                string.Equals(Path.GetFullPath(folder), Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase) ? null
                : folder);
            var bytes = File.ReadAllBytes(file);
            var pdbFile = Path.ChangeExtension(file, ".pdb");
            // a normal BepInEx plugin is rewritten by the wrapper module, when it's on (a rewritten copy has no pdb)
            int track = 0; string note = null; bool rewritten = false;
            if (Wrapper != null)
            {
                track = HotTrack.New();
                var w = Wrapper.Prepare(bytes, p.Loads + 1, track, out note, out var warnings, out var native, out var content);
                if (w != null) { bytes = w; p.Warnings.AddRange(warnings); p.WrapNative = native; p.LoadsContent = content; }
                else
                {
                    HotTrack.Discard(track); track = 0;
                    // a hot plugin with its own Il2Cpp types: renamed in each later copy, as wrapped ones are
                    var renamed = p.Loads > 0 ? Wrapper.RenameTypes(bytes, p.Loads + 1) : null;
                    if (renamed != null) { bytes = renamed; rewritten = true; note = "Il2Cpp types renamed"; }
                }
            }
            Assembly asm;
            using (var dll = new MemoryStream(bytes))
            {
                if (track == 0 && !rewritten
                    && File.Exists(pdbFile)) using (var pdb = new MemoryStream(File.ReadAllBytes(pdbFile))) asm = p.Ctx.LoadFromStream(dll, pdb);
                else asm = p.Ctx.LoadFromStream(dll);
            }
            p.Asm = asm;
            HotTrack.Watch(asm, file);                                    // its own Il2Cpp types' bridges are recorded, to be freed
            p.FileTime = File.GetLastWriteTimeUtc(file);
            // the same plugin also in BepInEx/plugins/ (loaded at start) would run twice: refuse, and say which to turn off
            var dup = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins.Values.FirstOrDefault(b =>
                string.Equals(Path.GetFileNameWithoutExtension(b.Location), asm.GetName().Name, StringComparison.OrdinalIgnoreCase) && File.Exists(b.Location));
            if (dup != null)
            {
                Unload(p, null);
                throw new InvalidOperationException(dup.Metadata.Name + " is also in BepInEx/plugins/ (" + dup.Location
                    + ") and already running: disable it there (BEPINEX PLUGINS tab) and restart, or don't load it hot");
            }
            var t = asm.GetTypes().FirstOrDefault(x => x.IsClass && !x.IsAbstract && x.GetConstructor(Type.EmptyTypes) != null
                && M(x, "Start", typeof(string)) != null && M(x, "Stop") != null && M(x, "Stop").ReturnType == typeof(string));
            if (t == null)
            {
                // the wrapper's object for it, which has Start and Stop (and passes on HandleHttp, HttpHelp, UpdateFeed)
                var adapter = track != 0 ? Wrapper.Adapt(asm, file, track) : null;
                if (adapter == null)
                {
                    if (track != 0) HotTrack.Discard(track);
                    throw new InvalidOperationException("no class with Start(string) and string Stop()" + (Wrapper == null
                        ? " (a normal BepInEx plugin runs here only while the LunarLink.Wrap module is on)" : ", and no BepInEx plugin class"));
                }
                p.Obj = adapter; t = adapter.GetType(); p.Wrapped = p.WasWrapped = true;
            }
            else
            {
                if (track != 0) { HotTrack.Discard(track); p.Warnings.Clear(); p.WrapNative = false; }
                p.Obj = Activator.CreateInstance(t);
            }
            p.StartM = M(t, "Start", typeof(string)); p.StopM = M(t, "Stop");
            p.UpdateM = M(t, "Update"); p.LateM = M(t, "LateUpdate");
            p.HttpM = M(t, "HandleHttp", typeof(string), typeof(string), typeof(NameValueCollection), typeof(string));
            if (p.HttpM != null && p.HttpM.ReturnType != typeof(string)) p.HttpM = null;
            p.HelpM = M(t, "HttpHelp"); if (p.HelpM != null && p.HelpM.ReturnType != typeof(string[])) p.HelpM = null;
            p.SettingsButtonsM = M(t, "SettingsButtons");
            if (p.SettingsButtonsM != null && p.SettingsButtonsM.ReturnType != typeof(string[])) p.SettingsButtonsM = null;
            p.ClickSettingButtonM = M(t, "ClickSettingButton", typeof(string));
            if (p.ClickSettingButtonM != null && p.ClickSettingButtonM.ReturnType != typeof(string)) p.ClickSettingButtonM = null;
            p.HasBoot = t.GetMethod("Boot", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null) != null;
            p.WorldM = M(t, "WorldLoaded"); p.WorldGoneM = M(t, "WorldUnloaded");
            p.Early = !p.Wrapped && StartsEarly(t);
            p.Loads++;
            Pump.Journal("hot: loaded " + p.Name + " (copy " + p.Loads + (p.Wrapped ? ", wrapped" + (note != null ? ", " + note : "") : "") + ")");
        }

        // public static bool StartEarly => true (a property or a field): start at game start, before any world
        static bool StartsEarly(Type t)
        {
            try
            {
                const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
                if (t.GetProperty("StartEarly", S) is PropertyInfo pi && pi.PropertyType == typeof(bool)) return (bool)pi.GetValue(null);
                if (t.GetField("StartEarly", S) is FieldInfo fi && fi.FieldType == typeof(bool)) return (bool)fi.GetValue(null);
            }
            catch (System.Exception caught) { Quiet.Note(caught); }
            return false;
        }

        // World events: WorldLoaded() each time a world has finished loading, and right after Start() when one already
        // has (so a plugin keeps its world set-up in one place); WorldUnloaded() once it's gone (the main menu).
        internal static bool InWorld => Pump.World() != null && !Pump.Loading();
        internal static void WorldChanged(bool loaded)
        {
            foreach (var p in plugins.Values.Where(x => x.Running).ToList()) World(p, loaded);
        }
        static void World(Plugin p, bool loaded)
        {
            var m = loaded ? p.WorldM : p.WorldGoneM; if (m == null) return;
            try { m.Invoke(p.Obj, null); }
            catch (Exception e) { var x = e.InnerException ?? e; LunarLink.Plugin.L.LogError("hot: " + p.Name + " " + m.Name + ": " + x); }
        }

        static MethodInfo M(Type t, string name, params Type[] args) => t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, args, null);

        static void Call(Plugin p, string staticName) => p.Obj.GetType().GetMethod(staticName,
            BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null).Invoke(null, null);

        static void Start(Plugin p, string state)
        {
            try
            {
                p.StartM.Invoke(p.Obj, new object[] { state });
                p.Running = true; Log("started " + p.Name + " (copy " + p.Loads + (p.Wrapped ? ", wrapped" : p.Early ? ", early" : "") + ")");
                if (InWorld) World(p, true);
            }
            catch (Exception e) { Fail(p, "Start", e); Unload(p, null); }   // whatever it did before throwing (patches, routes) goes
        }

        static void Frame(Plugin p, MethodInfo m)
        {
            try { m.Invoke(p.Obj, null); }
            catch (Exception e) { Fail(p, m.Name, e); }            // a throwing frame method turns the plugin off, logged once
        }

        static void Fail(Plugin p, string where, Exception e)
        {
            var x = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            p.Error = where + ": " + x;
            LunarLink.Plugin.L.LogError("hot: " + p.Name + " " + p.Error);
            if (p.Running) StopAndUnload(p);
        }

        // Stop the running copy, then drop it. Returns the state its Stop() handed over.
        static string StopAndUnload(Plugin p)
        {
            string state = null;
            if (p.Running)
            {
                p.Running = false;
                try { state = (string)p.StopM.Invoke(p.Obj, null); }
                catch (Exception e) { var x = e.InnerException ?? e; LunarLink.Plugin.L.LogError("hot: " + p.Name + " Stop: " + x); }
                if (p.Obj is IWrapped w) p.Leftovers.AddRange(w.Leftovers);
                else if (p.Asm != null)
                {
                    // whatever of its own types is still there is destroyed (journalled); one it destroyed itself in Stop() is
                    // only removed by Unity at the end of the frame, so it's not counted against the plugin
                    var (_, problem) = HotTrack.DestroyObjects(p.Asm);
                    if (problem != null) p.Leftovers.Add(problem);
                    HotTrack.Forget(p.Asm);
                }
            }
            return Unload(p, state);
        }

        static string Unload(Plugin p, string state)
        {
            ForceUnpatch(p);
            var asm = p.Asm;
            if (asm != null) { int n = Api.RemoveFrom(asm); if (n > 0) Pump.Journal("hot: " + p.Name + "'s " + n + " routes removed"); }
            p.State = state;
            var ctx = p.Ctx;
            p.Obj = null; p.Ctx = null; p.Asm = null; p.Wrapped = false; p.Early = false;
            p.StartM = p.StopM = p.UpdateM = p.LateM = p.HttpM = p.HelpM = p.SettingsButtonsM = p.ClickSettingButtonM = p.WorldM = p.WorldGoneM = null;
            if (ctx != null)
            {
                p.Old.Add(new WeakReference(ctx));
                if (ctx.IsCollectible) try { ctx.Unload(); } catch (Exception e) { p.Leftovers.Add("unload: " + e.Message); }
            }
            Pump.Journal("hot: unloaded " + p.Name);
            return state;
        }

        // Any Harmony patch whose owner is the plugin's name (or starts with "name.") or whose patch method lives in the
        // plugin's copy is still in after Stop(): take it out and report it.
        static void ForceUnpatch(Plugin p)
        {
            var asm = p.Asm;
            var owners = new HashSet<string>();
            foreach (var m in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(m);
                if (info == null) continue;
                foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                    if (patch.owner.Equals(p.Name, StringComparison.OrdinalIgnoreCase)
                        || patch.owner.StartsWith(p.Name + ".", StringComparison.OrdinalIgnoreCase)
                        || (asm != null && patch.PatchMethod?.DeclaringType?.Assembly == asm))
                        owners.Add(patch.owner);
            }
            foreach (var o in owners)
            {
                p.Leftovers.Add("Harmony patches still in under '" + o + "' after Stop(): removed");
                Harmony.UnpatchID(o);
            }
            if (owners.Count > 0) LunarLink.Plugin.L.LogWarning("hot: " + p.Name + " left Harmony patches in after Stop(): " + string.Join(", ", owners));
        }
    }
}
