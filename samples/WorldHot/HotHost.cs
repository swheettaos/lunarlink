// The normal BepInEx way in, so the same DLL ships as a regular plugin: BepInEx loads this class, which runs Main, calls
// its Update/LateUpdate, and tells it when a world loads or goes (LunarLink does all that for a hot copy, which never
// runs this class).
using System;
using System.Collections.Specialized;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace WorldHot
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class HotHost : BasePlugin
    {
        const string Guid = "example.worldhot", Name = "WorldHot", Version = "1.0.0";
        internal static Main Running;

        public override void Load()
        {
            Running = new Main(); Running.Start(null);
            ClassInjector.RegisterTypeInIl2Cpp<HotHostDriver>();
            var go = new GameObject(Name); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<HotHostDriver>();
        }

        public string HandleHttp(string method, string path, NameValueCollection query, string body) => Running?.HandleHttp(method, path, query, body);
        public string[] HttpHelp() => Running?.HttpHelp();
        public string UpdateFeed() => Running?.UpdateFeed();
    }

    public sealed class HotHostDriver : MonoBehaviour
    {
        public HotHostDriver(IntPtr ptr) : base(ptr) { }
        bool inWorld; float nextLook;
        void Update()
        {
            HotHost.Running?.Update();
            if (Time.realtimeSinceStartup < nextLook) return;                // once a second: a world loaded, or gone
            nextLook = Time.realtimeSinceStartup + 1f;
            bool now = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<MoonCleaners.MoonWorld>()).Length > 0;
            if (now == inWorld) return;
            inWorld = now;
            if (now) HotHost.Running?.WorldLoaded(); else HotHost.Running?.WorldUnloaded();
        }
        void LateUpdate() => HotHost.Running?.LateUpdate();
    }
}
