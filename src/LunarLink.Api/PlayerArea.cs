using System;
using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    // The player and the screen: where the player is and moving them, the observer camera, a line of text on screen,
    // and the game's own cleaning at a point.
    static class PlayerArea
    {
        internal static void Start(string state)
        {
            Api.Get("/player", "The local player: pos, forward, pitch, heading, camera forward", r => Player());
            Api.Post("/player/modify", "?x=&y=&z= : move the player there with the game's teleport (ignored while they're in a vehicle)", r => TeleportPlayer(r));
            Api.Get("/camera", "Which camera draws: the game's or the observer camera, and where it is", r => CameraState());
            Api.Post("/camera/modify", "?x=&y=&z=&tx=&ty=&tz=[&fov=][&hide=1] : the observer camera there, looking at t; hide=1 stops the game's camera drawing", r => CameraSet(r));
            Api.Post("/camera/remove", "Remove the observer camera and give the game's camera back", r => CameraOff());
            Api.Post("/message", "?text=[&ttl=15][&y=0.5][&size=20] : a line of text on screen; empty text hides it", r => ScreenMessage(r));
            Api.Post("/clean", "?x=&y=&z=&r=&a= : the game's own CleanAt round a point", r => Clean(Api.World, r));
        }

        // hand the camera back, show the look cards again and take the message down
        internal static void Stop()
        {
            try { if (observerCam != null) CameraOff(); HideLookPanels(); } catch { }
            if (msgLabel != null) { var layer = msgLabel.transform.parent != null ? msgLabel.transform.parent.gameObject : msgLabel; UnityEngine.Object.Destroy(layer); msgLabel = null; }
        }

        internal static void LateUpdate()
        {
            if (msgLabel != null && msgUntil > 0f && Time.realtimeSinceStartup > msgUntil && msgLabel.activeSelf) { msgLabel.SetActive(false); msgUntil = 0f; }
            try { HideLookPanels(); } catch (Exception e) { if (lookLogged != -2) { Plugin.L.LogWarning("look cards: " + e.Message); lookLogged = -2; } }
        }

        static string CameraState() => observerCam == null ? "{\"camera\":\"game\"}"
            : "{\"camera\":\"observer\",\"pos\":" + J.V(observerCam.transform.position) + ",\"forward\":" + J.V(observerCam.transform.forward) + "}";

        static string Player()
        {
            var b = Api.Player;
            if (b == null) return Api.NoPlayer;
            var cam = Camera.main;
            return "{\"pos\":" + J.V(b.transform.position) + ",\"forward\":" + J.V(b.transform.forward)
                + ",\"pitch\":" + J.F(b.pitch) + ",\"lookPitch\":" + J.F(b.LookPitch) + ",\"heading\":" + J.V(b.heading)
                + ",\"camForward\":" + (cam != null ? J.V(cam.transform.forward) : "null") + "}";
        }

        // POST /player/modify?x=&y=&z= : move the local player there with the game's own teleport (lands on the surface above that point)
        static string TeleportPlayer(Req req)
        {
            var b = Api.Player;
            if (b == null) return Api.NoPlayer;
            var p = new Vector3(Api.Qf(req, "x", 0), Api.Qf(req, "y", 0), Api.Qf(req, "z", 0));
            b.Teleport(p, true);
            return "{\"ok\":true,\"pos\":" + J.V(b.transform.position) + "}";
        }

        // Observer camera for recording (2026-11-03): ?x=&y=&z= (where it stands) &tx=&ty=&tz= (what it looks at)
        // [&fov=] [&hide=1: the game's own camera draws nothing underneath, cheaper]. A second camera, copied from
        // the game's, drawn on top and left where it is put - the player can be moved around by API clients
        // without the picture moving. POST /camera/remove removes it and gives the game's camera back.
        // goodMask: the game camera's own mask, kept from the first time it is seen drawing (never 0), so handing
        // the view back always restores it - a lost restore left the player seeing only the sky (2026-11-03)
        static GameObject observerCam; static int mainMask = -1, goodMask = 0; static Camera mainCam;
        static string CameraOff()
        {
            {
                if (observerCam != null) UnityEngine.Object.Destroy(observerCam);
                observerCam = null;
                int back = mainMask > 0 ? mainMask : goodMask;
                if (mainCam != null && back != 0) mainCam.cullingMask = back;
                var cur = Camera.main;
                if (cur != null && cur.cullingMask == 0 && goodMask != 0) cur.cullingMask = goodMask;
                mainMask = -1; mainCam = null;
                return "{\"ok\":true,\"camera\":\"game\",\"mask\":" + (cur != null ? cur.cullingMask : -1) + "}";
            }
        }

        static string CameraSet(Req req)
        {
            var main = Camera.main;
            if (observerCam == null)
            {
                observerCam = new GameObject("MoonObserverCamera");
                UnityEngine.Object.DontDestroyOnLoad(observerCam);
                var c = observerCam.AddComponent<Camera>();
                if (main != null) { c.CopyFrom(main); c.depth = main.depth + 10; if (c.cullingMask == 0 && goodMask != 0) c.cullingMask = goodMask; }
            }
            var cam = observerCam.GetComponent<Camera>();
            var pos = new Vector3(Api.Qf(req, "x", 0), Api.Qf(req, "y", 0), Api.Qf(req, "z", 0));
            var target = new Vector3(Api.Qf(req, "tx", 0), Api.Qf(req, "ty", 0), Api.Qf(req, "tz", 0));
            observerCam.transform.position = pos;
            observerCam.transform.rotation = Quaternion.LookRotation(target - pos, pos.normalized);
            cam.fieldOfView = Api.Qf(req, "fov", main != null ? main.fieldOfView : 60f);
            if (main != null && main.cullingMask != 0 && goodMask == 0) goodMask = main.cullingMask;
            if (main != null && Api.Qi(req, "hide", 0) == 1 && mainMask == -1 && main.cullingMask != 0) { mainCam = main; mainMask = main.cullingMask; main.cullingMask = 0; }
            return "{\"ok\":true,\"camera\":\"observer\",\"pos\":" + J.V(pos) + ",\"fov\":" + J.F(cam.fieldOfView) + "}";
        }

        // While the observer camera is on, the game's look-at info cards are kept hidden - the build moves the character
        // past machines all the time and the cards popping up spoil the picture (user, 2026-11-03). A card is a
        // MoonLookPanel (its own canvas and CanvasGroup), held by the machines (MoonProductionInteraction.look), the
        // capsule catchers (MoonCatcherLook.look) and the J.O.O.N.A.S. menu (MoonRobotMenu.joonasLook). They are looked
        // for every 2 s and faded out after every frame's game code has run (LateUpdate); the game sets them again by
        // itself once the observer camera is handed back. A card without a CanvasGroup has its canvas switched off
        // instead, and switched back on when the observer camera goes.
        static MoonLookPanel[] lookPanels = new MoonLookPanel[0]; static float lookNext = 0f; static int lookLogged = -1;
        static readonly System.Collections.Generic.List<GameObject> lookOff = new System.Collections.Generic.List<GameObject>();
        static void HideLookPanels()
        {
            if (observerCam == null)
            {
                if (lookOff.Count > 0) { foreach (var g in lookOff) if (g != null) g.SetActive(true); lookOff.Clear(); }
                return;
            }
            if (Time.realtimeSinceStartup >= lookNext)
            {
                lookNext = Time.realtimeSinceStartup + 2f;
                var ps = new System.Collections.Generic.List<MoonLookPanel>();
                var f1 = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonProductionInteraction>());
                for (int i = 0; i < f1.Length; i++) { var l = f1[i].Cast<MoonProductionInteraction>().look; if (l != null) ps.Add(l); }
                var f2 = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonCatcherLook>());
                for (int i = 0; i < f2.Length; i++) { var l = f2[i].Cast<MoonCatcherLook>().look; if (l != null) ps.Add(l); }
                var f3 = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonRobotMenu>());
                for (int i = 0; i < f3.Length; i++) { var l = f3[i].Cast<MoonRobotMenu>().joonasLook; if (l != null) ps.Add(l); }
                lookPanels = ps.ToArray();
                if (lookPanels.Length != lookLogged) { lookLogged = lookPanels.Length; Plugin.L.LogInfo($"observer: hiding {lookLogged} look cards"); }
            }
            foreach (var l in lookPanels)
            {
                if (l == null) continue;
                var g = l.group;
                if (g != null) { g.alpha = 0f; continue; }
                var c = l.canvas;
                if (c != null && c.activeSelf) { c.SetActive(false); if (!lookOff.Contains(c)) lookOff.Add(c); }
            }
        }

        // ?text= : a line of text at the top of the screen (status while a client builds); ?text= (empty) hides it.
        // A copy of one of the game's own text labels (so it has the game's font), made on request - nothing runs
        // every frame and nothing of the game's is changed. Only a Unity GameObject is kept.
        static GameObject msgLabel; static string msgSource = ""; static float msgY = 0.5f, msgSize = 20f;
        // &ttl= seconds a message stays up (default 15; 0 = until replaced) - messages stayed on screen forever (user, 2026-11-03)
        static float msgUntil = 0f;
        static string ScreenMessage(Req req)
        {
            var text = req.Query["text"] ?? "";
            try
            {
                if (msgLabel == null)
                {
                    var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<TMPro.TextMeshProUGUI>());
                    TMPro.TextMeshProUGUI src = null;
                    for (int i = 0; i < all.Length && src == null; i++) { var t = all[i].Cast<TMPro.TextMeshProUGUI>(); if (t.gameObject.activeInHierarchy && t.canvas != null) src = t; }
                    if (src == null) return "{\"ok\":false,\"reason\":\"no on-screen text to copy\"}";
                    // our own screen layer on top of everything (the game's canvases can be hidden or faded)
                    var layer = new GameObject("LunarLinkMessageLayer"); UnityEngine.Object.DontDestroyOnLoad(layer);
                    var cv = layer.AddComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay; cv.sortingOrder = 32000;
                    msgLabel = UnityEngine.Object.Instantiate(src.gameObject, layer.transform);
                    msgSource = src.gameObject.name + " on " + src.canvas.rootCanvas.gameObject.name;
                    msgLabel.name = "LunarLinkMessage";
                    var lt = msgLabel.GetComponent<TMPro.TextMeshProUGUI>();
                    lt.alignment = TMPro.TextAlignmentOptions.Center; lt.enableWordWrapping = false; lt.color = Color.white;
                }
                // where and how big: &y= (0 bottom .. 1 top, default 0.5 = the middle) &size= (default 20) - changeable on every call
                float y = Api.Qf(req, "y", msgY), size = Api.Qf(req, "size", msgSize); msgY = y; msgSize = size;
                var rt = msgLabel.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.15f, y - 0.03f); rt.anchorMax = new Vector2(0.85f, y + 0.03f); rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                var label = msgLabel.GetComponent<TMPro.TextMeshProUGUI>();
                label.enableAutoSizing = false; label.fontSizeMin = size; label.fontSizeMax = size; label.fontSize = size;   // the copied text auto-sized to its box
                msgLabel.transform.localScale = Vector3.one;
                label.text = text; msgLabel.SetActive(text.Length > 0); label.alpha = 1f; label.enabled = true;
                float ttl = Api.Qf(req, "ttl", 15f); msgUntil = ttl > 0 ? Time.realtimeSinceStartup + ttl : 0f;
                return "{\"ok\":true,\"copied\":" + J.S(msgSource) + ",\"screen\":[" + Screen.width + "," + Screen.height + "]}";
            }
            catch (Exception ex) { msgLabel = null; return "{\"ok\":false,\"reason\":" + J.S(ex.Message) + "}"; }
        }


        // /clean?x=&y=&z=&r=&a= : the game's own MoonWorld.CleanAt(point, radius, amount) - clears dust round a point
        // (the player's sweeping goes through it). Returns what the game returns (its count).
        static string Clean(MoonWorld w, Req req)
        {
            float F(string k, float d) => float.TryParse(req.Query[k], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
            var p = new Vector3(F("x", 0), F("y", 0), F("z", 0));
            int n = w.CleanAt(p, F("r", 4f), F("a", 1f));
            return "{\"ok\":true,\"returned\":" + n + "}";
        }

    }
}
