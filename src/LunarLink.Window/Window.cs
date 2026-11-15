using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LunarLink
{
    // Opening and closing, the keys and the mouse, the player frozen while the window is open, and running a button.
    static partial class Window
    {
        static BepInEx.Configuration.ConfigEntry<string> KeySetting => Plugin.WindowKey;          // kept in the core's config
        static BepInEx.Configuration.ConfigEntry<bool> ShowModules => Plugin.WindowShowModules;
        static string KeyLabel => KeySetting?.Value ?? "F9";

        enum Tab { HotMods, Mods, Api, Updates }
        static Tab tab;
        static string message;                                     // what the last button did (the line at the foot)

        static GameObject root;                                    // the window's canvas: null while it's closed
        static GraphicRaycaster raycaster;
        static GameObject ownEventSystem;                          // made when the game has none, removed with this copy
        static BaseInputModule gameInputModule; static bool gameInputModuleWasOn;
        static readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();   // button object id -> what it does
        static Key key = Key.None; static string keyName;
        static float nextRefresh; static string drawn;             // the list is checked once a second and redrawn on a change

        internal static void Update()
        {
            if (Restarting()) return;
            SavedThenMenu(); LoadAgain();
            if (reopenTab >= 0 && root == null) { tab = (Tab)reopenTab; reopenTab = -1; Open();
                message = "The window reloaded with the new build"; Draw(true); return; }
            var kb = Keyboard.current; if (kb == null) return;
            if (capturingKey) { CaptureKey(kb); return; }
            if (editing != null) { CaptureSetting(kb); return; }
            bool typing = false; try { typing = MoonInput.Typing; } catch (System.Exception caught) { Quiet.Note(caught); }      // not while typing in chat
            if (!typing && Pressed(kb)) { if (root == null) Open(); else Close(); return; }
            if (root == null) return;
            var mouse = Mouse.current;
            if (kb.escapeKey.wasPressedThisFrame || (mouse != null && mouse.rightButton.wasPressedThisFrame)) { Close(); return; }
            Click(); Tooltips();
            var wheel = mouse != null ? mouse.scroll.ReadValue().y : 0f;
            if (wheel != 0f) { ScrollBy(wheel); return; }
            if (Time.realtimeSinceStartup >= nextRefresh) Draw(false);
        }

        internal static void LateUpdate()
        {
            if (root == null) return;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;     // the game takes the cursor back each frame
            Animate();
        }

        static bool Pressed(Keyboard kb)
        {
            if (keyName != KeyLabel) { keyName = KeyLabel; if (!Enum.TryParse(keyName, true, out key)) key = Key.None; }
            return key != Key.None && kb.get_Item(key).wasPressedThisFrame;
        }

        static void Open()
        {
            var es = EventSystem.current;
            if (es == null) { ownEventSystem = new GameObject("LunarLink EventSystem");
                es = ownEventSystem.AddComponent<EventSystem>(); UnityEngine.Object.DontDestroyOnLoad(ownEventSystem); }
            gameInputModule = es.GetComponent<BaseInputModule>();
            if (gameInputModule != null) { gameInputModuleWasOn = gameInputModule.enabled; gameInputModule.enabled = false; }
            message = null;
            Freeze();
            Draw(true);
        }

        static void Close()
        {
            DestroyPane();
            capturingKey = false; editing = null; restartAt = 0f;
            if (gameInputModule != null) { gameInputModule.enabled = gameInputModuleWasOn; gameInputModule = null; }
            Unfreeze();
        }

        // a reload: the window was open, on which tab, and when (a copy started much later, turned off and on again,
        // doesn't reopen it)
        static int reopenTab = -1;
        internal static string Handover() => root != null ? "#window " + (int)tab + " " + DateTime.UtcNow.Ticks + "\n" : "";
        internal static void TakeOver(string state)
        {
            var line = (state ?? "").Split('\n').FirstOrDefault(l => l.StartsWith("#window "));
            var p = line?.Split(' ');
            if (p != null && p.Length == 3 && int.TryParse(p[1], out var t) && long.TryParse(p[2], out var at)
                && DateTime.UtcNow - new DateTime(at, DateTimeKind.Utc) < TimeSpan.FromSeconds(15)) reopenTab = t;
        }

        // the module stopping (a reload, or turned off): the window closes, and its sprites go with this copy
        internal static void Stop()
        {
            if (root != null) Close();
            if (ownEventSystem != null) { UnityEngine.Object.Destroy(ownEventSystem); ownEventSystem = null; }
            DestroySprites();
        }

        // A button's action: what it returns (or the error it threw) shows at the foot, then the window redraws.
        static void Act(Func<string> what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { message = what() + " (" + clock.ElapsedMilliseconds + " ms)"; }
            catch (Exception e) { message = "Error: " + e.Message; Plugin.L.LogWarning("window: " + e); }
            if (root != null) Draw(true);          // a reload of this module from its own row has closed this copy
        }

        static string Done(Hot.Plugin p, string ok) => p == null ? "Not found" : p.Error != null ? "Failed: " + Short(p.Error) : ok;

        static string Short(string s) { s = (s ?? "").Split('\n')[0]; return s.Length > 110 ? s.Substring(0, 110) + "…" : s; }

        static void SaveConfig()
        {
            try { ShowModules?.ConfigFile?.Save(); }
            catch (Exception e) { Plugin.L.LogWarning("window: config not saved: " + e.Message); }
        }

        // A left-click press: runs the button under the mouse.
        static void Click()
        {
            var mouse = Mouse.current; var es = EventSystem.current;
            if (raycaster == null || mouse == null || es == null || !mouse.leftButton.wasPressedThisFrame) return;
            var ev = new PointerEventData(es) { position = mouse.position.ReadValue() };
            var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>(); raycaster.Raycast(ev, hits);
            var en = hits.GetEnumerator();
            while (en.MoveNext())
            {
                var b = en.Current.gameObject?.GetComponentInParent<UnityEngine.UI.Button>();
                if (b != null && b.interactable && actions.TryGetValue(b.gameObject.GetInstanceID(), out var act)) { act(); return; }
            }
        }

        // ------------------------------------------------ the player held still while the window is open
        // As Quantum Conveyors does: the walker blocked and its input asset off (the cursor alone isn't enough: movement,
        // jump, tools and interaction would still take WASD and the mouse).
        static CursorLockMode lockBefore; static bool visibleBefore;
        static MoonWalker walker; static bool walkerWasBlocked, walkerWasOn;
        static InputActionAsset playerInput; static bool playerInputWasOn;
        static readonly List<InputActionAsset> otherInput = new List<InputActionAsset>();

        static void Freeze()
        {
            lockBefore = Cursor.lockState; visibleBefore = Cursor.visible;
            var found = FindWalker();
            if (found != null)
            {
                walkerWasBlocked = found.UIBlocked; walkerWasOn = found.enabled;
                playerInput = found.inputTemplate;
                if (playerInput != null) { playerInputWasOn = playerInput.enabled; playerInput.Disable(); }
                else DisableOtherInput();
                found.UIBlocked = true;
            }
            else DisableOtherInput();
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        static void Unfreeze()
        {
            try { var found = FindWalker(); if (found != null) { found.UIBlocked = walkerWasBlocked; found.enabled = walkerWasOn; } }
            catch (Exception e) { Plugin.L.LogWarning("window: player not restored: " + e.Message); }
            try { if (playerInput != null && playerInputWasOn) playerInput.Enable(); }
            catch (Exception e) { Plugin.L.LogWarning("window: player input not restored: " + e.Message); }
            playerInput = null;
            foreach (var asset in otherInput) try { if (asset != null) asset.Enable(); } catch (System.Exception caught) { Quiet.Note(caught); }
            otherInput.Clear();
            Cursor.lockState = lockBefore; Cursor.visible = visibleBefore;
        }

        // the player's walker: on the main camera, else anywhere in the loaded scenes
        static MoonWalker FindWalker()
        {
            if (walker != null) return walker;
            var cam = Camera.main ?? Camera.current;
            if (cam != null)
            {
                var found = cam.GetComponent(Il2CppType.Of<MoonWalker>()) ?? cam.GetComponentInParent(Il2CppType.Of<MoonWalker>())
                    ?? cam.transform.root.GetComponentInChildren(Il2CppType.Of<MoonWalker>());
                walker = found?.TryCast<MoonWalker>();
                if (walker != null) return walker;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i); if (!scene.isLoaded) continue;
                foreach (var top in scene.GetRootGameObjects())
                {
                    walker = top.GetComponentInChildren(Il2CppType.Of<MoonWalker>())?.TryCast<MoonWalker>();
                    if (walker != null) return walker;
                }
            }
            return null;
        }

        // no walker found (the main menu): every enabled input asset but the UI's
        static void DisableOtherInput()
        {
            foreach (var item in Resources.FindObjectsOfTypeAll(Il2CppType.Of<InputActionAsset>()))
            {
                var asset = item.TryCast<InputActionAsset>();
                var n = asset?.name ?? "";
                if (asset == null || !asset.enabled || n.IndexOf("ui", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                asset.Disable(); otherInput.Add(asset);
            }
        }
    }
}
