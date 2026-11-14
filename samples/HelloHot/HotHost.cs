// The normal BepInEx way in, so the same DLL ships as a regular plugin: BepInEx loads this class, which runs Main and
// calls its Update/LateUpdate from a MonoBehaviour of its own (only here - a hot loader never runs this class, and
// drives Main itself). Copy this file next to your Main and change the three names below.
using System;
using System.Collections.Specialized;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace HelloHot
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class HotHost : BasePlugin
    {
        const string Guid = "example.hellohot", Name = "HelloHot", Version = "1.0.0";
        internal static Main Running;

        public override void Load()
        {
            // a plugin that registers MoonFramework buildings or cargo does it here, before Start (a static Boot() on Main)
            Running = new Main(); Running.Start(null);
            ClassInjector.RegisterTypeInIl2Cpp<HotHostDriver>();
            var go = new GameObject(Name); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<HotHostDriver>();
        }

        // so a mod serving HTTP can pass requests on to the plugin when it's installed the normal way too
        public string HandleHttp(string method, string path, NameValueCollection query, string body) => Running?.HandleHttp(method, path, query, body);
        public string[] HttpHelp() => Running?.HttpHelp();
        public string UpdateFeed() => Running?.UpdateFeed();
    }

    public sealed class HotHostDriver : MonoBehaviour
    {
        public HotHostDriver(IntPtr ptr) : base(ptr) { }
        void Update() => HotHost.Running?.Update();
        void LateUpdate() => HotHost.Running?.LateUpdate();
    }
}
