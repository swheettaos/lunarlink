// HelloHot: a small plugin that reloads in place, as an example and a template. Copy the folder, rename it and the
// class names, and put your work in Main. It shows each piece of the contract:
//   Start(state) / Stop()      start, and undo everything Start did (here: a Harmony patch), handing on what to keep
//   Update() / LateUpdate()    every frame (no MonoBehaviour of your own while it's loaded hot)
//   HandleHttp / HttpHelp      endpoints of its own, described for /help
//   UpdateFeed                 where its builds are, so LunarLink can update it (optional)
// HotHost.cs makes the same DLL a normal BepInEx plugin for players.
using System.Collections.Specialized;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HelloHot
{
    public class Main
    {
        // one Harmony id for everything this plugin patches: Stop() takes it all out, and a loader can find leftovers by it
        internal const string Id = "HelloHot";
        static int productionSteps;                     // counted by the patch below
        Harmony harmony;
        int frames, starts;

        public void Start(string state)
        {
            int.TryParse(state, out starts);            // what the previous copy's Stop() handed over (null the first time)
            starts++;
            harmony = new Harmony(Id);
            harmony.Patch(typeof(MoonCleaners.MoonWorld).GetMethod("StepProduction", new[] { typeof(float) }),
                postfix: new HarmonyMethod(typeof(Main).GetMethod(nameof(AfterStep), BindingFlags.Static | BindingFlags.NonPublic)));
            Debug.Log($"HelloHot started (start {starts})");
        }

        public string Stop()
        {
            harmony?.UnpatchSelf(); harmony = null;      // undo everything Start did
            return starts.ToString();                   // the next copy's state
        }

        public void Update() => frames++;
        public void LateUpdate() { }

        static void AfterStep() => productionSteps++;

        public string[] HttpHelp() => new[] { "GET /hello | HelloHot: how many times it started, frames and production steps since its last start" };

        // Where this plugin's builds are, so LunarLink can update it from F9's UPDATES tab (optional; null = no updates):
        //   "github:you/yourplugin"                      your GitHub repo's latest release (its .zip)
        //   "https://example.com/yourplugin/update.json" any public HTTPS address of an update file (version, file, SHA-256)
        public string UpdateFeed() => null;

        public string HandleHttp(string method, string path, NameValueCollection query, string body)
        {
            if (path != "/hello") return null;          // not ours: the next plugin gets it
            if (method != "GET") return "{\"error\":\"use GET\"}";
            return "{\"ok\":true,\"starts\":" + starts + ",\"frames\":" + frames + ",\"productionSteps\":" + productionSteps + "}";
        }
    }
}
