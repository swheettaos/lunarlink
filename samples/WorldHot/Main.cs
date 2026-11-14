// WorldHot: an example of a hot plugin that sets up the world, the way a plugin like Mars Sweepers does. It shows:
//   StartEarly                  start at game start, before any world (where BepInEx starts normal plugins), so its
//                               patches are in before a save loads; Start() must not assume a world then
//   WorldLoaded() / WorldUnloaded()   set the world up and let it go: called each time a save finishes loading and once
//                               it's gone, and WorldLoaded() also right after Start() when a world is already loaded, so
//                               a copy reloaded mid-game sets the world up exactly as a fresh load would
//   its own Unity type          WorldMarker, a MonoBehaviour registered for Il2Cpp; LunarLink renames it in each later
//                               copy (WorldMarker_hot2...) so copies don't clash, and frees the old one
//   Stop() -> state             counters handed to the next copy (a real plugin hands over its machines' state)
using System;
using System.Collections.Specialized;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace WorldHot
{
    public class Main
    {
        public static bool StartEarly => true;          // start before any world (LunarLink 1.3.0 and later)

        int starts, worldsLoaded, worldsGone; string startedWhere;
        GameObject marker;                              // what it adds to a world

        public void Start(string state)
        {
            if (state != null) { var p = state.Split(','); int.TryParse(p[0], out starts); int.TryParse(p[1], out worldsLoaded); int.TryParse(p[2], out worldsGone); }
            starts++;
            startedWhere = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<MoonCleaners.MoonWorld>()).Length == 0 ? "before any world" : "in a world";
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp<WorldMarker>()) ClassInjector.RegisterTypeInIl2Cpp<WorldMarker>();
            Debug.Log($"WorldHot started {startedWhere} (start {starts})");
        }

        // a world has finished loading (or was already loaded when this copy started): set it up
        public void WorldLoaded()
        {
            worldsLoaded++;
            marker = new GameObject("WorldHot marker");
            marker.AddComponent<WorldMarker>();
            Debug.Log($"WorldHot: world loaded ({worldsLoaded}); marker {marker.GetComponent<WorldMarker>().GetIl2CppType().Name} placed");
        }

        // the world is gone (the main menu): let go of what was set up in it
        public void WorldUnloaded()
        {
            worldsGone++;
            if (marker != null) UnityEngine.Object.Destroy(marker);
            marker = null;
        }

        public string Stop()
        {
            if (marker != null) UnityEngine.Object.Destroy(marker);   // undo what Start and WorldLoaded did
            marker = null;
            return starts + "," + worldsLoaded + "," + worldsGone;
        }

        public void Update() { }
        public void LateUpdate() { }

        public string[] HttpHelp() => new[] { "GET /worldhot | WorldHot: starts, where it last started, worlds loaded and gone, its marker's type" };
        public string UpdateFeed() => null;

        public string HandleHttp(string method, string path, NameValueCollection query, string body)
        {
            if (path != "/worldhot") return null;
            string type = null; try { type = marker != null ? marker.GetComponent<WorldMarker>().GetIl2CppType().Name : null; } catch { }
            return "{\"ok\":true,\"starts\":" + starts + ",\"startedWhere\":\"" + startedWhere + "\",\"worldsLoaded\":" + worldsLoaded
                + ",\"worldsGone\":" + worldsGone + ",\"marker\":" + (type == null ? "null" : "\"" + type + "\"") + "}";
        }
    }

    // its own Unity type: allowed in a hot plugin from LunarLink 1.3 (each copy's is renamed)
    public class WorldMarker : MonoBehaviour
    {
        public WorldMarker(IntPtr ptr) : base(ptr) { }
    }
}
