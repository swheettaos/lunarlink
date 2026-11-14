// The wrapper: runs normal BepInEx plugins put in BepInEx/hot/ like hot plugins, so they can be reloaded and turned off
// while the game runs. It fills the core's wrapper slot (Hot.Wrapper) while it runs: at game start from Boot(), before
// any plugin it wraps loads, and again from Start() after a reload. A plugin wrapped by an earlier copy of this module
// keeps that copy's Adapter (and so that copy in memory) until the plugin is reloaded or turned off.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Unity.IL2CPP;

namespace LunarLink
{
    public class WrapModule : IWrapper
    {
        static readonly WrapModule slot = new WrapModule();

        public static void Boot() => Hot.Wrapper = slot;
        public void Start(string state) => Hot.Wrapper = slot;
        public string Stop() { if (Hot.Wrapper == slot) Hot.Wrapper = null; return null; }

        public byte[] Prepare(byte[] dll, int n, int track, out string note, out string[] warnings, out bool native, out bool content)
        {
            note = null; warnings = null; native = false; content = false;
            if (!Rewriter.IsCandidate(dll)) return null;
            if (Rewriter.NameOf(dll) == "MoonFramework")
                throw new InvalidOperationException("MoonFramework must stay in BepInEx/plugins/ and can't be hot-swapped: other plugins are built on it, and it runs the modded buildings in your world. Move it back there and restart");
            var dirs = Rewriter.SearchDirs().Where(d => !d.EndsWith("core") && !d.EndsWith("interop")).ToList();
            if (Rewriter.UsedBy(dll, dirs) is List<string> users)
                throw new InvalidOperationException("other plugins are built on it (" + string.Join(", ", users.Take(4)) + (users.Count > 4 ? "..." : "")
                    + "), so it's a library: it must stay in BepInEx/plugins/ and can't be hot-swapped. Move it back there and restart");
            var bytes = Rewriter.Rewrite(dll, n, track, Rewriter.SearchDirs(), out var s);
            if (n > 1 && s.Injected.Count > 0) note = "Il2Cpp types renamed";
            content = s.Bundles > 0;
            warnings = (s.Summary != null ? new[] { s.Summary } : new string[0]).Concat(s.Warnings)
                .Concat(content ? new[] { "loads its own content (asset bundles) when the world loads: in a world, Reload waits for the main menu" } : new string[0]).ToArray();
            native = s.Native;
            return bytes;
        }

        public byte[] RenameTypes(byte[] dll, int n) => Rewriter.RenameTypes(dll, n);

        public object Adapt(Assembly asm, string file, int track)
        {
            var t = Adapter.Types(asm).FirstOrDefault(x => x.IsClass && !x.IsAbstract && typeof(BasePlugin).IsAssignableFrom(x)
                && x.GetCustomAttribute<BepInEx.BepInPlugin>() != null && x.GetConstructor(Type.EmptyTypes) != null);
            if (t == null) return null;
            HotTrack.Watch(asm, file);
            return new Adapter(t, track);
        }
    }
}
