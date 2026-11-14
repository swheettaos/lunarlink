// The rewrite: a normal BepInEx plugin's DLL, changed as it's loaded so that what it starts can be undone (the calls go
// to the core's HotTrack, see Track.cs there), and from its second copy on with its Il2Cpp types renamed (Il2Cpp keeps
// a registered type's name for good: Driver -> Driver_hot2). Also reports what it can't track: native hooks made some
// other way (turning the plugin off or reloading it then waits for a restart) and long-running tasks.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace LunarLink
{
    static class Rewriter
    {
        // What the rewrite found and did
        internal sealed class Scan
        {
            public readonly List<string> Injected = new List<string>();   // types made for Il2Cpp (a constructor taking an IntPtr)
            public bool Native;                                            // native hooks the rewrite can't track
            public int Threads, Timers, Detours, Events, Bundles;          // calls now tracked
            public int Locations;                                          // assembly.Location reads, now answered with the DLL's path
            public readonly List<string> Warnings = new List<string>();
            public string Summary
            {
                get
                {
                    var tracked = new[] { (Threads, "thread"), (Timers, "timer"), (Detours, "native hook"), (Events, "event subscription"), (Bundles, "asset bundle load") }
                        .Where(x => x.Item1 > 0).Select(x => x.Item1 + " " + x.Item2 + (x.Item1 == 1 ? "" : "s")).ToList();
                    var parts = new List<string>();
                    if (tracked.Count > 0) parts.Add("tracked: " + string.Join(", ", tracked) + " (undone when it's turned off)");
                    if (Locations > 0) parts.Add("asks for its own location: told its DLL's path, so files beside it are found");
                    return parts.Count == 0 ? null : string.Join("; ", parts);
                }
            }
        }

        // A normal BepInEx plugin, not a hot one: a [BepInPlugin] class, and no class with Start(string) and string Stop()
        internal static bool IsCandidate(byte[] dll)
        {
            using var m = ModuleDefinition.ReadModule(new MemoryStream(dll));
            var types = m.GetTypes().ToList();
            bool hot = types.Any(t => t.IsPublic && t.IsClass && !t.IsAbstract
                && t.Methods.Any(x => x.Name == "Start" && x.IsPublic && !x.IsStatic && x.Parameters.Count == 1 && x.Parameters[0].ParameterType.FullName == "System.String")
                && t.Methods.Any(x => x.Name == "Stop" && x.IsPublic && !x.IsStatic && x.Parameters.Count == 0 && x.ReturnType.FullName == "System.String"));
            return !hot && types.Any(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
        }

        static bool IsInjected(TypeDefinition t) => t.BaseType != null && t.BaseType.FullName != "System.Object" && t.Methods.Any(c => c.IsConstructor
            && !c.IsStatic && c.Parameters.Count == 1 && c.Parameters[0].ParameterType.FullName == "System.IntPtr");

        // a reference type from another assembly (its events outlive the plugin's copy)
        static bool Outside(TypeReference t, ModuleDefinition m)
        {
            TypeDefinition d; try { d = t.Resolve(); } catch { return false; }
            return d != null && !d.IsValueType && d.Module != m;
        }

        static bool IsDetourType(TypeReference t)
        {
            if (t == null) return false;
            var n = t.FullName;
            return n.StartsWith("BepInEx.Unity.IL2CPP.Hook.") || n.StartsWith("MonoMod.RuntimeDetour.");
        }

        // Rewrite a wrapped plugin for copy n (n > 1: its Il2Cpp types renamed) with HotTrack bag id. Returns the new DLL.
        internal static byte[] Rewrite(byte[] dll, int n, int id, IEnumerable<string> dirs, out Scan s)
        {
            s = new Scan();
            using var resolver = new DefaultAssemblyResolver();               // writing resolves its references (enum constants)
            foreach (var d in dirs.Where(Directory.Exists)) resolver.AddSearchDirectory(d);
            resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location));
            using var m = ModuleDefinition.ReadModule(new MemoryStream(dll), new ReaderParameters { AssemblyResolver = resolver });
            var track = typeof(HotTrack);
            var keep = m.ImportReference(track.GetMethod(nameof(HotTrack.Keep)));
            var ev = m.ImportReference(track.GetMethod(nameof(HotTrack.Event)));
            var location = m.ImportReference(track.GetMethod(nameof(HotTrack.Location)));

            foreach (var t in m.GetTypes().ToList())
            {
                if (IsInjected(t)) { s.Injected.Add(t.FullName); if (n > 1) t.Name += "_hot" + n; }
                foreach (var method in t.Methods.Where(x => x.HasBody).ToList())
                {
                    var body = method.Body; var il = body.GetILProcessor();
                    bool changed = false;
                    for (int i = 0; i < body.Instructions.Count; i++)
                    {
                        var ins = body.Instructions[i];
                        if (!(ins.Operand is MethodReference mr)) continue;
                        var decl = mr.DeclaringType.FullName;
                        if (!changed) { body.SimplifyMacros(); changed = true; }
                        // assembly.Location -> HotTrack.Location(assembly): the DLL's path, for plugins that read files beside it
                        if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && mr.Name == "get_Location" && decl == "System.Reflection.Assembly")
                        {
                            ins.OpCode = OpCodes.Call; ins.Operand = location; s.Locations++;
                        }
                        // new Thread(start[, maxStack]) -> HotTrack.NewThread(start[, maxStack], id)
                        else if (ins.OpCode == OpCodes.Newobj && decl == "System.Threading.Thread")
                        {
                            var args = mr.Parameters.Select(p => p.ParameterType.FullName == "System.Int32" ? typeof(int)
                                : p.ParameterType.FullName == "System.Threading.ThreadStart" ? typeof(System.Threading.ThreadStart) : typeof(System.Threading.ParameterizedThreadStart)).Append(typeof(int)).ToArray();
                            var nt = track.GetMethod(nameof(HotTrack.NewThread), args);
                            if (nt == null) continue;
                            ins.OpCode = OpCodes.Ldc_I4; ins.Operand = id;
                            il.InsertAfter(ins, il.Create(OpCodes.Call, m.ImportReference(nt)));
                            s.Threads++; i++;
                        }
                        // timers and native detours: kept, to be disposed
                        else if ((ins.OpCode == OpCodes.Newobj && (decl == "System.Threading.Timer" || decl == "System.Timers.Timer" || IsDetourType(mr.DeclaringType)))
                            || ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && IsDetourType(mr.ReturnType)))
                        {
                            var after = new[] { il.Create(OpCodes.Dup), il.Create(OpCodes.Ldc_I4, id), il.Create(OpCodes.Call, keep) };
                            var at = ins; foreach (var x in after) { il.InsertAfter(at, x); at = x; }
                            if (decl.Contains("Timer")) s.Timers++; else s.Detours++;
                            i += after.Length;
                        }
                        // anything that hands back an AssetBundle (Unity's loaders, an async request's assetBundle, or the plugin's
                        // own helper around Unity's internal loader, as Mars Sweepers has): kept, to be unloaded. Unity loads a
                        // bundle only once, so a copy that left its bundle loaded stops the next copy from loading it.
                        else if (((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && mr.ReturnType.FullName == "UnityEngine.AssetBundle")
                            || (ins.OpCode == OpCodes.Newobj && decl == "UnityEngine.AssetBundle"))
                        {
                            var after = new[] { il.Create(OpCodes.Dup), il.Create(OpCodes.Ldc_I4, id), il.Create(OpCodes.Call, keep) };
                            var at = ins; foreach (var x in after) { il.InsertAfter(at, x); at = x; }
                            s.Bundles++; i += after.Length;
                        }
                        // x.add_Event(handler) on another assembly's type -> the same, then HotTrack.Event(x, handler, remove_Event)
                        else if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && mr.Name.StartsWith("add_") && mr.Parameters.Count == 1
                            && Outside(mr.DeclaringType, m) && !(i > 0 && body.Instructions[i - 1].OpCode == OpCodes.Constrained))
                        {
                            var remove = new MethodReference("remove_" + mr.Name.Substring(4), mr.ReturnType, mr.DeclaringType) { HasThis = mr.HasThis };
                            remove.Parameters.Add(new ParameterDefinition(mr.Parameters[0].ParameterType));
                            var pt = mr.Parameters[0].ParameterType;
                            var h = new VariableDefinition(pt.ContainsGenericParameter ? m.TypeSystem.Object : pt); body.Variables.Add(h);
                            VariableDefinition target = null;
                            if (mr.HasThis) { target = new VariableDefinition(mr.DeclaringType.ContainsGenericParameter ? m.TypeSystem.Object : mr.DeclaringType); body.Variables.Add(target); }
                            var call = il.Create(ins.OpCode, mr);
                            // the add call becomes "store the handler", so branches to it still land on the start
                            ins.OpCode = OpCodes.Stloc; ins.Operand = h;
                            var seq = new List<Instruction>();
                            if (target != null) { seq.Add(il.Create(OpCodes.Stloc, target)); seq.Add(il.Create(OpCodes.Ldloc, target)); }
                            seq.Add(il.Create(OpCodes.Ldloc, h)); seq.Add(call);
                            seq.Add(target != null ? il.Create(OpCodes.Ldloc, target) : il.Create(OpCodes.Ldnull));
                            seq.Add(il.Create(OpCodes.Ldloc, h));
                            seq.Add(il.Create(OpCodes.Ldtoken, remove));
                            seq.Add(il.Create(OpCodes.Ldtoken, mr.DeclaringType));
                            seq.Add(il.Create(OpCodes.Ldc_I4, id));
                            seq.Add(il.Create(OpCodes.Call, ev));
                            var at = ins; foreach (var x in seq) { il.InsertAfter(at, x); at = x; }
                            s.Events++; i += seq.Count;
                        }
                    }
                    if (changed) body.OptimizeMacros();
                }
            }
            // native hooks the rewrite can't track
            var native = new List<string>();
            if (m.GetMemberReferences().Any(r => r.Name == "GetFunctionPointerForDelegate")) native.Add("Marshal.GetFunctionPointerForDelegate");
            foreach (var t in m.GetTypes()) foreach (var x in t.Methods.Where(x => x.IsPInvokeImpl && x.PInvokeInfo != null))
                if (x.Name.Contains("Protect") || x.PInvokeInfo.Module.Name.ToLowerInvariant().Contains("dobby") || x.PInvokeInfo.Module.Name.ToLowerInvariant().Contains("minhook"))
                    native.Add(x.PInvokeInfo.Module.Name + "!" + x.Name);
            if (native.Count > 0)
            {
                s.Native = true;
                s.Warnings.Add("hooks game code natively some other way (" + string.Join(", ", native.Distinct().Take(3)) + "): turning it off or reloading it waits for a restart");
            }
            if (m.GetMemberReferences().Any(r => r.DeclaringType?.FullName == "System.Threading.Tasks.TaskCreationOptions" || r.Name == "UnsafeStart"))
                s.Warnings.Add("may start long-running tasks: turning it off may not stop them");
            var o = new MemoryStream(); m.Write(o); return o.ToArray();
        }

        // A hot plugin's own Il2Cpp types renamed for copy n (Il2Cpp keeps a registered name for good); null when it has
        // none. Nothing else is touched: a hot plugin undoes what it starts itself.
        internal static byte[] RenameTypes(byte[] dll, int n)
        {
            using var m = ModuleDefinition.ReadModule(new MemoryStream(dll));
            var injected = m.GetTypes().Where(IsInjected).ToList();
            if (injected.Count == 0 || n < 2) return null;
            foreach (var t in injected) t.Name += "_hot" + n;
            var o = new MemoryStream(); m.Write(o); return o.ToArray();
        }

        // A library other plugins are built against (MoonFramework is the usual one) can't be hot: plugins loaded the
        // normal way can't use code that can be unloaded (.NET refuses), and a reloaded copy would be a different
        // assembly to every plugin built against the old one. Returns the names of the plugins that use it, or null.
        internal static string NameOf(byte[] dll)
        {
            using var m = ModuleDefinition.ReadModule(new MemoryStream(dll));
            return m.Assembly.Name.Name;
        }

        internal static List<string> UsedBy(byte[] dll, IEnumerable<string> dirs)
        {
            var name = NameOf(dll);
            var users = new List<string>();
            foreach (var d in dirs.Where(Directory.Exists).Distinct())
                foreach (var f in Directory.GetFiles(d, "*.dll"))
                {
                    if (Path.GetFileNameWithoutExtension(f) == name) continue;
                    try
                    {
                        using var other = ModuleDefinition.ReadModule(f);
                        if (other.AssemblyReferences.Any(r => r.Name == name)) users.Add(Path.GetFileNameWithoutExtension(f));
                    }
                    catch { }                                              // not a .NET DLL
                }
            return users.Count == 0 ? null : users.Distinct().OrderBy(x => x).ToList();
        }

        // Where a plugin's references are: BepInEx, the game's interop assemblies, every plugin folder and hot/
        internal static IEnumerable<string> SearchDirs()
        {
            var root = BepInEx.Paths.BepInExRootPath;
            yield return Path.Combine(root, "core"); yield return Path.Combine(root, "interop"); yield return Path.Combine(root, "hot");
            var plugins = Path.Combine(root, "plugins");
            if (Directory.Exists(plugins)) { yield return plugins; foreach (var d in Directory.GetDirectories(plugins, "*", SearchOption.AllDirectories)) yield return d; }
        }

    }
}
