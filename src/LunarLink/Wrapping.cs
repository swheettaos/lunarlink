// What the core asks of the wrapper module (LunarLink.Wrap), which runs normal BepInEx plugins put in BepInEx/hot/.
// The module fills Hot.Wrapper while it runs. Everything a wrapped plugin's rewritten code calls lives in the core
// (HotTrack, Track.cs), so reloading the module never pulls the ground from under a running wrapped plugin.
using System.Collections.Generic;
using System.Reflection;

namespace LunarLink
{
    internal interface IWrapper
    {
        // A normal BepInEx plugin's DLL rewritten for copy n, its calls tracked in HotTrack bag track; null when the DLL
        // isn't one. note: for the journal ("Il2Cpp types renamed"); warnings: for F9; native: hooks it can't undo;
        // content: it loads asset bundles (its reloads in a world wait for the main menu).
        byte[] Prepare(byte[] dll, int n, int track, out string note, out string[] warnings, out bool native, out bool content);

        // The hot object for a loaded rewritten copy: Start(string), string Stop(), and HandleHttp, HttpHelp and
        // UpdateFeed passed on to the plugin. Null when it has no BepInEx plugin class.
        object Adapt(Assembly asm, string file, int track);
    }

    // The wrapper's hot object: what its Stop() couldn't undo, for the leftovers list.
    internal interface IWrapped
    {
        IEnumerable<string> Leftovers { get; }
    }
}
