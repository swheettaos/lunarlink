// The hot plugin manager window (the LunarLink.Window hot module, so it reloads like any module): a key (F9 by default, "Window key" in LunarLink's config) opens and closes it. One
// row per DLL in BepInEx/hot/ with its state, build time, a "newer build on disk" mark and any error, and Enable/Disable
// and Reload buttons; along the top Reload changed and Rescan. The buttons run the same actions as the /hot/* API.
// LunarLink's own modules are hidden unless "Show LunarLink modules" is ticked (kept in the config). At most
// MaxRows rows show at once; the mouse wheel or a click on the scroll bar moves through the rest.
// A second tab lists the normal BepInEx plugins (BepPlugins): Disable/Enable there takes effect at the next game start.
// A third, API, lists every endpoint (GET /help, asked through the core's route table: the core's, each module area's,
// each plugin's), one area at a time. Without the Api module there's no /help, and the tab says so.
// A fourth, UPDATES, shows LunarLink's releases and module builds and installs them (the LunarLink.Update module,
// reached through the core's Updates slot).
// Unity's own UI on a canvas of its own, made when the window opens and destroyed when it closes, sized for 1920 x 1080
// and scaled by height. Buttons act on the mouse press, found by a raycast (no onClick listeners).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LunarLink
{
    // The hot module: the window is driven from here, and Stop() closes it (and removes the EventSystem it made).
    public class WindowModule
    {
        // what a reload hands over: whether the window was open and on which tab (a "#window" line), then the BepInEx
        // plugins turned on this session; an update that reloads this module reopens the window where it was, so its
        // result shows
        public void Start(string state) { BepPlugins.TakeOver(state); HotWindow.TakeOver(state); BepPlugins.Routes(); }
        public string Stop() { var open = HotWindow.Handover(); HotWindow.Stop(); return open + BepPlugins.Handover(); }
        public void Update() => HotWindow.Update();
        public void LateUpdate() => HotWindow.LateUpdate();
    }

    static class HotWindow
    {
        static BepInEx.Configuration.ConfigEntry<string> KeySetting => Plugin.WindowKey;          // kept in the core's config
        static BepInEx.Configuration.ConfigEntry<bool> ShowModules => Plugin.WindowShowModules;

        // Quantum Conveyors' menu look: a cream window whose rows are cream too, told apart by a soft gradient (orange for
        // something live, dark otherwise) and rounded corners; dark text; grey buttons with light text; orange accent; dark
        // tooltips. Text is the light text on buttons and tooltips; on the cream, Ink and Dim.
        static readonly Color Panel = new Color(0.937f, 0.922f, 0.882f, 1f), TipBg = new Color(0.165f, 0.165f, 0.196f, 1f),
            Btn = new Color(0.286f, 0.286f, 0.294f, 1f), Accent = new Color(1f, 0.478f, 0.18f, 1f), Text = new Color(0.949f, 0.929f, 0.894f, 1f),
            Ink = new Color(0.11f, 0.133f, 0.196f, 1f), Dim = new Color(0.47f, 0.45f, 0.43f, 1f), Faint = new Color(0.729f, 0.71f, 0.686f, 1f),
            Track = new Color(0.84f, 0.82f, 0.78f, 1f), Red = new Color(0.8f, 0.24f, 0.2f, 1f), Green = new Color(0.18f, 0.55f, 0.28f, 1f);
        const float W = 1040f, RowH = 76f, Gap = 8f, Head = 60f; const int MaxRows = 7;
        static int first;                      // the first row shown (scrolling)
        static int tab;                        // 0 hot plugins, 1 BepInEx plugins, 2 API (every endpoint, GET /help), 3 updates
        static bool newUi = true;              // the Quantum-style pane (the default); OLD UI switches back
        static int newUiTab;                   // 0 Link Plugins, 1 Other Plugins, 2 API, 3 Updates
        static bool bepTab => tab == 1;
        static string apiGroup;                // API tab: the area or plugin shown (null = all)

        // one row as drawn, whichever tab it comes from
        sealed class View
        {
            public string Name, State, Detail, Tip; public Color StateColour; public bool Disabled, Warm, UpdateAvailable;     // Tip: a "?" on the row
            public List<(string label, Action act, bool accent)> Buttons = new List<(string, Action, bool)>();
        }

        static GameObject layer, newUiCanvasObject; static RectTransform panel; static GraphicRaycaster ray, newUiRay;
        static Canvas newUiCanvas;
        static readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        static Key key = Key.None; static string keyName;
        static float nextRefresh; static string drawn, message;
        static CursorLockMode lockBefore; static bool visibleBefore;
        static BaseInputModule previousInputModule; static bool previousInputModuleEnabled;
        static MoonWalker walker; static bool previousWalkerBlocked, previousWalkerEnabled;
        static InputActionAsset playerInput; static bool previousPlayerInputEnabled;
        static readonly List<InputActionAsset> fallbackInputAssets = new List<InputActionAsset>();
        static readonly HashSet<string> newUiOpenGroups = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> newUiOpenModules = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> newUiOpenBepPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> newUiOpenApiEndpoints = new HashSet<string>(StringComparer.Ordinal);
        static bool newUiAnimateNext, newUiAutoGroup;
        static string newUiOpened;              // the row just opened (its key), for the scroll after it
        // the settings page in place of the list: one plugin's settings (its config file), opened from its row's Settings
        // button; Back (or a tab) returns to the list where it was
        sealed class SettingsPage { public string Title, Name, Guid, File; public bool Hot; public float ScrollBefore; }
        static SettingsPage newUiSettingsPage;
        static bool newUiCapturingKey;
        static float newUiScroll, newUiScrollTarget, newUiScrollVelocity, newUiScrollAnimationFrom, newUiScrollAnimationTo, newUiScrollAnimationUntil;
        static readonly HashSet<string> newUiQueuedGradientRows = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> newUiQueuedReloadRows = new HashSet<string>(StringComparer.Ordinal);
        static RectTransform newUiContent, newUiViewport;
        static RectTransform newUiTransition;
        static CanvasGroup newUiTransitionGroup;
        static float newUiTransitionUntil, newUiTransitionFadeDuration = 0.22f;
        static bool newUiTransitionRemoveOnly;
        static UnityEngine.UI.Text newUiModuleButtonText;
        static Image newUiModuleButtonImage;
        static UnityEngine.UI.Text newUiKeyInputText;
        static Image newUiKeyInputImage;
        static PluginSettings.Entry newUiEditingSetting;
        static string newUiSettingDraft;
        static UnityEngine.UI.Text newUiSettingInputText;
        static bool newUiCapturingSetting;
        static RectTransform newUiRestartButton;
        static UnityEngine.UI.Text newUiRestartButtonText;
        static bool newUiRestartWaiting;
        static float newUiRestartQuitAt;
        static GameObject newUiFooterObject, newUiKeyRowObject;
        static readonly Image[] newUiTabImages = new Image[4];
        static readonly UnityEngine.UI.Text[] newUiTabTexts = new UnityEngine.UI.Text[4];
        static readonly List<System.Text.Json.JsonElement> newUiGroups = new List<System.Text.Json.JsonElement>();
        static readonly List<System.Text.Json.JsonElement> newUiChannels = new List<System.Text.Json.JsonElement>();
        static int newUiRow;
        static Image newUiTopFade, newUiBottomFade;
        static Sprite newUiTopFadeSprite, newUiBottomFadeSprite;
        sealed class UiRowTween { public RectTransform Row; public Vector2 From, To; public CanvasGroup FadeGroup; public float FadeFrom, FadeTo, FadeStart, FadeUntil, Until; }
        sealed class UiImageTween { public Image Image; public float From, To, Start, Until; }
        static readonly List<UiRowTween> newUiRowTweens = new List<UiRowTween>();
        static readonly List<UiImageTween> newUiImageTweens = new List<UiImageTween>();
        static readonly Dictionary<string, RectTransform> newUiLiveRows = new Dictionary<string, RectTransform>(StringComparer.Ordinal);
        static Sprite newUiFolderIcon;

        internal static void Update()
        {
            if (newUiRestartWaiting)
            {
                if (Time.unscaledTime >= newUiRestartQuitAt) FinishNewUiRestart();
                return;
            }
            SavedThenMenu(); LoadAgain();
            if (reopenTab >= 0 && layer == null) { tab = reopenTab; reopenTab = -1; Open(); message = "The window reloaded with the new build"; Draw(true); return; }
            var kb = Keyboard.current; if (kb == null) return;
            if (newUiCapturingKey) { CaptureNewUiKey(kb); return; }
            if (newUiCapturingSetting) { CaptureNewUiSetting(kb); return; }
            bool typing = false; try { typing = MoonCleaners.MoonInput.Typing; } catch { }   // not while typing in chat
            if (!typing && Pressed(kb)) { if (layer == null) Open(); else Close(); return; }
            if (layer == null) return;
            var mouse = Mouse.current;
            if (kb.escapeKey.wasPressedThisFrame || (mouse != null && mouse.rightButton.wasPressedThisFrame)) { Close(); return; }
            Click(); Tooltips();
            var wheel = mouse != null ? mouse.scroll.ReadValue().y : 0f;
            if (wheel != 0f)
            {
                if (newUi) ScrollNewUi(wheel);
                else Scroll(wheel > 0 ? -1 : 1);
                return;
            }
            if (Time.realtimeSinceStartup >= nextRefresh) Draw(false);
        }

        internal static void LateUpdate()
        {
            if (layer != null) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            for (int i = newUiRowTweens.Count - 1; i >= 0; i--)
            {
                var a = newUiRowTweens[i]; if (a.Row == null) { newUiRowTweens.RemoveAt(i); continue; }
                float t = Mathf.Clamp01(1f - (a.Until - Time.unscaledTime) / 0.22f);
                float smooth = t * t * (3f - 2f * t);
                a.Row.anchoredPosition = Vector2.LerpUnclamped(a.From, a.To, smooth);
                if (a.FadeGroup != null)
                {
                    float fadeT = Mathf.Clamp01((Time.unscaledTime - a.FadeStart) / Mathf.Max(0.001f, a.FadeUntil - a.FadeStart));
                    float fadeSmooth = fadeT * fadeT * (3f - 2f * fadeT);
                    a.FadeGroup.alpha = Mathf.LerpUnclamped(a.FadeFrom, a.FadeTo, fadeSmooth);
                }
                if (t >= 1f) newUiRowTweens.RemoveAt(i);
            }
            if (newUiScrollAnimationUntil > 0f && newUiContent != null)
            {
                float t = Mathf.Clamp01(1f - (newUiScrollAnimationUntil - Time.unscaledTime) / 0.22f);
                float smooth = t * t * (3f - 2f * t);
                newUiScroll = Mathf.LerpUnclamped(newUiScrollAnimationFrom, newUiScrollAnimationTo, smooth);
                newUiContent.anchoredPosition = new Vector2(0, newUiScroll);
                if (newUiTransition != null) newUiTransition.anchoredPosition = new Vector2(0, newUiScroll);
                UpdateNewUiFades();
                if (t >= 1f) newUiScrollAnimationUntil = 0f;
            }
            else if (newUiContent != null && Mathf.Abs(newUiScroll - newUiScrollTarget) > 0.01f)
            {
                float max = Mathf.Max(0f, newUiContent.rect.height - newUiViewport.rect.height);
                newUiScrollTarget = Mathf.Clamp(newUiScrollTarget, 0f, max);
                newUiScroll = Mathf.SmoothDamp(newUiScroll, newUiScrollTarget, ref newUiScrollVelocity, 0.09f, 2600f, Time.unscaledDeltaTime);
                if (Mathf.Abs(newUiScroll - newUiScrollTarget) < 0.05f)
                {
                    newUiScroll = newUiScrollTarget;
                    newUiScrollVelocity = 0f;
                }
                newUiContent.anchoredPosition = new Vector2(0f, newUiScroll);
                if (newUiTransition != null) newUiTransition.anchoredPosition = new Vector2(0f, newUiScroll);
                UpdateNewUiFades();
            }
            for (int i = newUiImageTweens.Count - 1; i >= 0; i--)
            {
                var a = newUiImageTweens[i]; if (a.Image == null) { newUiImageTweens.RemoveAt(i); continue; }
                float t = Mathf.Clamp01((Time.unscaledTime - a.Start) / Mathf.Max(0.001f, a.Until - a.Start));
                float smooth = t * t * (3f - 2f * t);
                var color = a.Image.color; color.a = Mathf.LerpUnclamped(a.From, a.To, smooth); a.Image.color = color;
                if (t >= 1f) newUiImageTweens.RemoveAt(i);
            }
            if (newUiTransitionUntil > 0 && newUiTransitionGroup != null)
            {
                if (newUiTransitionRemoveOnly)
                {
                    if (Time.unscaledTime >= newUiTransitionUntil)
                    {
                        for (int i = newUiTransition.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(newUiTransition.GetChild(i).gameObject);
                        newUiTransitionGroup.alpha = 1f; newUiTransitionUntil = 0f; newUiTransitionRemoveOnly = false;
                    }
                }
                else
                {
                    float t = Mathf.Clamp01(1f - (newUiTransitionUntil - Time.unscaledTime) / Mathf.Max(0.001f, newUiTransitionFadeDuration));
                    float smooth = t * t * (3f - 2f * t);
                    newUiTransitionGroup.alpha = 1f - smooth;
                    if (t >= 1f)
                    {
                        for (int i = newUiTransition.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(newUiTransition.GetChild(i).gameObject);
                        newUiTransitionGroup.alpha = 1f; newUiTransitionUntil = 0f;
                    }
                }
            }
        }

        static bool Pressed(Keyboard kb)
        {
            var setting = KeySetting?.Value ?? "F9";
            if (keyName != setting) { keyName = setting; if (!Enum.TryParse(setting, true, out key)) key = Key.None; }
            return key != Key.None && kb.get_Item(key).wasPressedThisFrame;
        }

        static void Open()
        {
            lockBefore = Cursor.lockState; visibleBefore = Cursor.visible;
            layer = new GameObject("LunarLink hot plugins"); UnityEngine.Object.DontDestroyOnLoad(layer);
            var c = layer.AddComponent<Canvas>(); c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = 31000;
            var sc = layer.AddComponent<CanvasScaler>(); sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080); sc.matchWidthOrHeight = 1f;
            ray = layer.AddComponent<GraphicRaycaster>();
            var es = EventSystem.current;
            if (es == null) { ownEventSystem = new GameObject("LunarLink EventSystem"); es = ownEventSystem.AddComponent<EventSystem>(); UnityEngine.Object.DontDestroyOnLoad(ownEventSystem); }
            previousInputModule = es.GetComponent<BaseInputModule>();
            if (previousInputModule != null) { previousInputModuleEnabled = previousInputModule.enabled; previousInputModule.enabled = false; }
            message = null; first = 0;
            Freeze();
            Draw(true);
        }

        static GameObject ownEventSystem;

        // the module stopping (a reload, or turned off): the window closes and the cursor goes back
        // a reload: open, which tab, and when (a copy started much later, say turned off and on again, doesn't reopen)
        static int reopenTab = -1;
        internal static string Handover() => layer != null ? "#window " + tab + " " + DateTime.UtcNow.Ticks + "\n" : "";
        internal static void TakeOver(string state)
        {
            var line = (state ?? "").Split('\n').FirstOrDefault(l => l.StartsWith("#window "));
            var p = line?.Split(' ');
            if (p != null && p.Length == 3 && int.TryParse(p[1], out var t) && long.TryParse(p[2], out var at)
                && DateTime.UtcNow - new DateTime(at, DateTimeKind.Utc) < TimeSpan.FromSeconds(15)) reopenTab = t;
        }

        internal static void Stop()
        {
            if (layer != null) Close();
            if (ownEventSystem != null) { UnityEngine.Object.Destroy(ownEventSystem); ownEventSystem = null; }
            // the sprites go with this copy of the module, so a reload leaves no textures behind
            foreach (var s in new[] { rounded, button, circle, warm, cool, grey, pill, newUiFolderIcon, newUiTopFadeSprite, newUiBottomFadeSprite })
                if (s != null) { UnityEngine.Object.Destroy(s.texture); UnityEngine.Object.Destroy(s); }
            rounded = button = circle = warm = cool = grey = pill = newUiFolderIcon = null;
            newUiTopFadeSprite = newUiBottomFadeSprite = null;
            newUiLiveRows.Clear();
            newUiQueuedGradientRows.Clear();
            newUiQueuedReloadRows.Clear();
            newUiImageTweens.Clear(); newUiTransitionRemoveOnly = false;
        }

        static void Close()
        {
            actions.Clear(); tips.Clear();
            if (layer != null) UnityEngine.Object.Destroy(layer);
            if (newUiCanvasObject != null) UnityEngine.Object.Destroy(newUiCanvasObject);
            newUiCanvasObject = null; newUiCanvas = null; newUiRay = null;
            layer = null; panel = null; ray = null; drawn = null;
            newUiModuleButtonText = null; newUiModuleButtonImage = null;
            newUiKeyInputText = null; newUiKeyInputImage = null; newUiCapturingKey = false;
            newUiEditingSetting = null; newUiSettingDraft = null; newUiSettingInputText = null; newUiCapturingSetting = false;
            newUiRestartButton = null;
            newUiRestartButtonText = null; newUiRestartWaiting = false;
            newUiLiveRows.Clear();
            newUiQueuedGradientRows.Clear();
            newUiQueuedReloadRows.Clear();
            newUiImageTweens.Clear(); newUiTransitionRemoveOnly = false;
            newUiScroll = newUiScrollTarget = newUiScrollVelocity = 0f;
            if (previousInputModule != null) { previousInputModule.enabled = previousInputModuleEnabled; previousInputModule = null; }
            Unfreeze();
            Cursor.lockState = lockBefore; Cursor.visible = visibleBefore;
        }

        // GET /help through the core's route table (the help lives in the Api module); empty when that module isn't running
        static List<(string group, string from, string method, string path, string help)> Endpoints()
        {
            var list = new List<(string, string, string, string, string)>();
            string json;
            try { json = Api.Dispatch(new Req { Method = "GET", Path = "/help", Query = new System.Collections.Specialized.NameValueCollection() }, "/help"); }
            catch { json = null; }
            if (json == null) return list;
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
                foreach (var g in doc.RootElement.GetProperty("groups").EnumerateArray())
                    foreach (var e in g.GetProperty("endpoints").EnumerateArray())
                        list.Add((g.GetProperty("group").GetString(), g.GetProperty("from").GetString(), e.GetProperty("method").GetString(),
                                  e.GetProperty("path").GetString(), e.GetProperty("help").GetString()));
            return list;
        }

        static bool Show(Hot.Row r) => (ShowModules?.Value ?? false) || !r.Name.StartsWith("LunarLink.", StringComparison.Ordinal);

        static void Scroll(int by)
        {
            int n = Views(out _, out _).Count;
            int to = Math.Max(0, Math.Min(first + by, n - MaxRows));
            if (to != first) { first = to; Draw(true); }
        }

        static Color StateColour(string state) => state == "running" ? Green : state == "failed" ? Red : state.Contains("next start") || state.Contains("main menu") ? Accent : state == "disabled" || state == "off" ? Dim : Ink;

        // The rows of the tab in view; hidden = LunarLink modules left out, modules = how many there are.
        static List<View> Views(out int hidden, out int modules)
        {
            var list = new List<View>();
            if (tab == 3)
            {
                hidden = modules = 0;
                list.AddRange(UpdateRows());
                return list;
            }
            if (tab == 2)
            {
                hidden = modules = 0;
                foreach (var e in Endpoints())
                {
                    if (apiGroup != null && e.group != apiGroup) continue;
                    list.Add(new View { Name = e.method + " " + e.path, State = e.group, StateColour = e.from == "core" ? Ink : e.from.Contains("plugin") ? Accent : Green, Detail = e.help });
                }
                return list;
            }
            if (bepTab)
            {
                hidden = modules = 0;
                foreach (var e in BepPlugins.List())
                {
                    var ordinary = e.State == "running" || e.State == "off" || e.State == "off from next start";
                    var v = new View { Name = DisplayNames.For(e.Name) + (e.Version != null ? "  " + e.Version : ""), State = ordinary ? "" : e.State, StateColour = StateColour(e.State),
                                       Disabled = e.State == "off" || e.State == "off from next start", Warm = e.State != "off" && e.State != "off from next start",
                                       Detail = e.File + (e.NeededBy.Count > 0 ? "   needed by " + string.Join(", ", e.NeededBy.Select(DisplayNames.For)) : "") + (e.State.Contains("next start") ? "   <color=#FF7A2E>restart the game to apply</color>" : "") };
                    var file = e.File;
                    if (e.Self) v.Detail += "   (runs this window)";
                    else if (e.State == "running") v.Buttons.Add(("Disable", () => Act(() => BepPlugins.Disable(file) ?? "Turned off: " + file + ". RESTART THE GAME to apply (it keeps running until then)"), false));
                    else if (e.State == "off" || e.State == "off from next start") v.Buttons.Add(("Enable", () => Act(() => BepPlugins.Enable(file) ?? (e.Loaded ? "Turned back on: " + file + ". It never stopped, so no restart is needed" : "Turned on: " + file + ". RESTART THE GAME to apply")), true));
                    // turned on by mistake before a restart: take it back (nothing loaded it, so nothing to restart for)
                    else if (e.State == "on from next start") v.Buttons.Add(("Disable", () => Act(() => BepPlugins.Disable(file) ?? "Turned back off: " + file + ". It never loaded, so no restart is needed"), false));
                    list.Add(v);
                }
                return list;
            }
            var all = Hot.Rows();
            modules = all.Count(r => r.Name.StartsWith("LunarLink.", StringComparison.Ordinal));
            var rows = all.Where(Show).ToList(); hidden = all.Count - rows.Count;
            foreach (var r in rows)
            {
                var v = new View { Name = DisplayNames.For(r.Name), State = r.State == "running" || r.State == "disabled" ? "" : r.State, StateColour = StateColour(r.State),
                    Disabled = r.State == "disabled", Warm = r.State != "disabled", UpdateAvailable = r.Newer,
                    Detail = r.Error != null ? "<color=#E6524A>" + Short(r.Error) + "</color>"
                        : (FileVersion(r.Name) is string fv ? V(fv) + "   ·   " : "") + (r.Built != null ? "built " + r.Built : "") + (r.Newer ? "   <color=#FF7A2E>newer build on disk</color>" : "") + (r.NeedsRestart ? "   restart to register buildings" : "")
                        + (r.Wrapped ? "   <color=#FF7A2E>wrapped (experimental)</color>" : "") };
                if (r.Wrapped) v.Tip = "A normal BepInEx plugin, run by LunarLink's wrapper (experimental): it loads like BepInEx would, and Reload and Disable undo what it started. Test them once before relying on them. Details: the wiki's The Wrapper page."
                    + (r.Warnings.Length > 0 ? "\n\n" + string.Join("\n", r.Warnings.Select(w => "• " + w)) : "\n\nNothing found that the wrapper can't undo.");
                if (r.State == "off at restart") v.Detail += "   <color=#FF7A2E>off from the next start</color>";
                bool waits = r.State == "reloads at main menu";
                if (waits) v.Detail += "   <color=#FF7A2E>new copy loads at the main menu (or the next start)</color>";
                bool off = r.State == "disabled" || r.State == "off at restart"; var name = r.Name; var display = DisplayNames.For(name);
                v.Buttons.Add((off ? "Enable" : "Disable", () => Act(() => off ? Done(Hot.Enable(name, reload: false), "Enabled " + display) : Done(Hot.Disable(name), "Disabled " + display)), off));
                // a wrapped plugin that loads its own content, in a world: Reload waits for the main menu (Hot.Waits), no forcing
                if (waits)
                {
                    v.Buttons.Clear();
                    // solo: the whole round trip (save, menu, reload, the same save again); with a crew, leaving the world ends
                    // the session for everyone, so it stops at the menu
                    // the game keeps a Steam lobby even solo (invite-only, you the host): a crew is someone else in it
                    bool crew = false;
                    try { crew = MoonCleaners.MoonSteamLobby.InLobby && Steamworks.SteamMatchmaking.GetNumLobbyMembers(new Steamworks.CSteamID(MoonCleaners.MoonSteamLobby.LobbyId)) > 1; } catch { }
                    if (crew) v.Buttons.Add(("Save & menu", () => Act(() => SaveThenMenu(null)), true));
                    else v.Buttons.Add(("Save & reload", () => Act(() => SaveThenMenu(name)), true));
                    v.Buttons.Add(("Cancel", () => Act(() => Done(Hot.CancelReload(name), "No reload of " + name)), false));
                }
                else v.Buttons.Add(("Reload", () => Act(() =>
                {
                    var pl = Hot.Enable(name, reload: true);
                    return pl != null && pl.ReloadAtMenu ? name + " " + Hot.WaitReason(pl) + ", so its new copy loads at the main menu (in a world it couldn't save properly). Save & reload does it all and brings you back to this save"
                        : Done(pl, "Reloaded " + display);
                }), false));
                list.Add(v);
            }
            return list;
        }

        // Rebuilds the window when what it shows has changed (checked once a second, and after every button).
        static void Draw(bool force)
        {
            // A reload can deliver one final Update call while the previous
            // window has already been torn down. Ignore that harmless frame.
            if (layer == null) return;
            nextRefresh = Time.realtimeSinceStartup + 1f;
            var rows = Views(out int hidden, out int modules);
            first = Math.Max(0, Math.Min(first, rows.Count - MaxRows));
            var sig = newUi + "|" + newUiTab + "|" + tab + "|" + apiGroup + "|" + string.Join("|", rows.Select(r => r.Name + r.State + r.Detail)) + "|" + message + "|" + first + "|" + hidden + "|" + ReadUpdates().busy + "|" + ReadUpdates().Waiting;
            if (!force && sig == drawn) return;
            drawn = sig;
            if (newUi && panel != null && newUiContent != null)
            {
                RefreshNewUiContent();
                return;
            }
            actions.Clear(); tips.Clear();
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            panel = null; newUiContent = null; newUiViewport = null; newUiTransition = null; newUiTransitionGroup = null;
            if (newUi)
            {
                DrawNewUiPane();
                return;
            }
            // one size on every tab and filter: room for MaxRows rows and the message line, the scroll bar for more
            int shown = Math.Min(MaxRows, rows.Count);
            float listH = MaxRows * (RowH + Gap);
            float h = Head + 190f + listH + 30f;
            panel = Rect("Window", layer.transform, new Vector2(W, h), Vector2.zero, Panel);
            ray = layer.GetComponent<GraphicRaycaster>();
            UiVersionToggle(panel, W, h);
            // the header: the title in the middle, a "?" beside it, the close cross on the right
            Label(panel, "Lunar Link", new Vector2(0, h / 2 - 38), new Vector2(400, 56), 32, Ink, TextAnchor.MiddleCenter);
            string keyLabel = KeySetting?.Value ?? "F9";
            Help(panel, new Vector2(110, h / 2 - 38),
                "<b>HOT PLUGINS</b>: the DLLs in BepInEx/hot/. Enable, Disable (it leaves memory) and Reload, all while the game runs.\n" +
                "<b>BEPINEX PLUGINS</b>: the normal plugins in BepInEx/plugins/. Turning one off or on applies at the next game start.\n" +
                "<b>API</b>: every HTTP request on 127.0.0.1:8765, from LunarLink, its modules and plugins.\n" +
                "<b>UPDATES</b>: new LunarLink releases, module builds and plugin versions. Only a new core needs a restart.\n" +
                "<b>Support file</b>: saves a zip in BepInEx/ to attach to a bug report (names and ids blanked out).\n\n" +
                keyLabel + " opens and closes this window; the mouse wheel scrolls the list.",
                new Vector2(660, 250), new Vector2(0, h / 2 - Head - 140));
            Button(panel, "×", new Vector2(W / 2 - 38, h / 2 - 36), new Vector2(52, 50), Close);
            // the support file (the core's GET /support): everything needed for a bug report, names blanked out
            Button(panel, "Support file", new Vector2(-W / 2 + 112, h / 2 - 36), new Vector2(190, 46), () => Act(() =>
            {
                var (file, problems, error) = Support.Save();
                return error != null ? "Couldn't write the support file: " + error
                    : "Saved BepInEx/" + System.IO.Path.GetFileName(file) + ": attach it to a GitHub issue" + (problems.Count > 0 ? " (" + problems.Count + " problem" + (problems.Count == 1 ? "" : "s") + " found)" : "");
            }));
            float top = h / 2f - Head;
            // the tabs: pills, the open one orange
            int waiting = ReadUpdates().Waiting;          // a count on the UPDATES tab when something has an update
            float tx = -(230 + 260 + 130 + 180 + 3 * 16) / 2f;
            Tab(panel, "HOT PLUGINS", tab == 0, new Vector2(tx + 115, top - 35), () => { tab = 0; first = 0; message = null; Draw(true); }, 230,
                "Hot Plugins are Lunar Link-compatible modules that can be enabled, disabled, reloaded, or updated while the game is running. A module may still need a restart if it adds game buildings or other startup-only content."); tx += 246;
            Tab(panel, "BEPINEX PLUGINS", tab == 1, new Vector2(tx + 130, top - 35), () => { tab = 1; first = 0; message = null; Draw(true); }, 260,
                "BepInEx Plugins are traditional mods in BepInEx/plugins. Lunar Link can rename their DLLs to enable or disable them, but those changes apply at the next game start because normal plugins cannot be safely unloaded."); tx += 276;
            Tab(panel, "API", tab == 2, new Vector2(tx + 65, top - 35), () => { tab = 2; first = 0; message = null; Draw(true); }, 130,
                "API is the advanced/developer view. It lists the local HTTP endpoints exposed by Lunar Link and compatible plugins. Most players can ignore this tab; it is useful for automation, diagnostics, and mod development."); tx += 146;
            Tab(panel, "UPDATES" + (waiting > 0 ? "  <color=" + (tab == 3 ? "#1C2232" : "#FF7A2E") + ">●" + waiting + "</color>" : ""), tab == 3, new Vector2(tx + 90, top - 35), () => { tab = 3; first = 0; message = null; Draw(true); }, 180,
                "Updates checks the configured feeds for Lunar Link, compatible modules, and supported plugins. Hot modules can reload in place; a new Lunar Link core or a normal BepInEx plugin requires a restart.\n\nOnly Lunar Link-compatible mods appear here. If a mod is missing, install one compatible release manually first.");
            if (tab == 2)
            {
                // one button steps through the areas and plugins (All, LunarLink, World, ... , each plugin)
                var groups = Endpoints().Select(e => e.group).Distinct().ToList();
                Button(panel, "Showing: " + (apiGroup ?? "All") + "  ›", new Vector2(-W / 2 + 205, top - 134), new Vector2(340, 40), () =>
                {
                    int i = apiGroup == null ? -1 : groups.IndexOf(apiGroup);
                    apiGroup = i + 1 < groups.Count ? groups[i + 1] : null; first = 0; Draw(true);
                });
                Label(panel, rows.Count + " endpoint" + (rows.Count == 1 ? "" : "s") + "   ·   green: LunarLink's modules, orange: plugins'",
                    new Vector2(-W / 2 + 640, top - 134), new Vector2(560, 28), 17, Dim, TextAnchor.MiddleLeft);
            }
            else if (tab == 3)
            {
                var u = ReadUpdates();
                int newer = u.modules.Count(m => m.state == "available");
                if (u.on && !u.busy && u.state != "restart")              // nothing to check or install until the new core has loaded
                {
                    Button(panel, "Check for updates", new Vector2(W / 2 - 180, top - 88), new Vector2(260, 42), () => Act(() => { Updates.Check?.Invoke(); return "Asking GitHub for LunarLink's latest release and module builds..."; }), true);
                    if (newer > 1) Button(panel, "Update all modules", new Vector2(W / 2 - 180, top - 134), new Vector2(260, 40), () => Act(() => { Updates.Apply?.Invoke("all"); return "Updating " + newer + " modules..."; }));
                }
                // manual by default; ticked, it checks once at every game start
                if (u.on) Checkbox(panel, "Check for updates at game start", u.auto, new Vector2(-W / 2 + 226, top - 134), () =>
                {
                    Updates.Apply?.Invoke(u.auto ? "auto:0" : "auto:1"); Draw(true);
                });
                Label(panel, "Only a new core needs a restart.", new Vector2(60, top - 134), new Vector2(300, 28), 16, Dim, TextAnchor.MiddleLeft);
            }
            else if (!bepTab)
            {
                Button(panel, "↻", new Vector2(W / 2 - 88, top - 88), new Vector2(44, 44), () => Act(() =>
                {
                    var (added, removed) = Hot.RescanAll();
                    return added.Count + removed.Count == 0 ? "No new or removed DLLs" : "Added " + added.Count + ", removed " + removed.Count;
                }), true);
                ToggleButton(panel, "Show LunarLink modules (" + modules + ")", ShowModules?.Value ?? false, new Vector2(-W / 2 + 250, top - 134), () =>
                {
                    if (ShowModules != null) ShowModules.Value = !ShowModules.Value;
                    SaveWindowSettings();
                    first = 0; Draw(true);
                });
                Button(panel, "Reload All", new Vector2(W / 2 - 156, -h / 2 + 38), new Vector2(180, 42), () => Act(() =>
                {
                    var done = Hot.ReloadChanged();
                    return done.Count == 0 ? "No newer builds on disk" : "Reloaded " + string.Join(", ", done.Select(p => DisplayNames.For(p.Name)));
                }), true);
            }
            else
            {
                int pending = rows.Count(r => r.State.Contains("next start"));
                Label(panel, pending > 0 ? "<color=#FF7A2E><b>RESTART REQUIRED:</b> " + pending + " change" + (pending == 1 ? "" : "s") + " take effect at the next game start</color>"
                                         : "Plugins load at game start: Disable and Enable apply at the next start (restart required).",
                    new Vector2(-W / 2 + 380, top - 134), new Vector2(720, 28), 18, Dim, TextAnchor.MiddleLeft);
            }
            float listTop = top - 155f;
            if (rows.Count == 0)
                Label(panel, tab == 2 ? "No /help: the LunarLink.Api module isn't running" : hidden > 0 ? "Only LunarLink's modules are in BepInEx/hot/ (tick the box to see them)" : bepTab ? "No plugins" : "No DLLs in BepInEx/hot/",
                    new Vector2(0, listTop - RowH / 2), new Vector2(W - 60, RowH), 22, Dim, TextAnchor.MiddleCenter);
            float y = listTop;
            for (int i = first; i < first + shown; i++) { DrawRow(rows[i], y - RowH / 2); y -= RowH + Gap; }
            if (rows.Count > MaxRows) ScrollBar(listTop, listH - Gap, rows.Count, shown);
            if (message != null) Label(panel, message, new Vector2(-W / 2 + 280, -h / 2 + 30), new Vector2(520, 36), 19, Ink, TextAnchor.MiddleLeft);
        }

        static void EnsureNewUiCanvas()
        {
            string stage = "checking existing canvas";
            try
            {
                // A hot reload can leave the Unity object reference alive for one
                // frame after the managed Canvas field has been torn down. Rebuild
                // the pair instead of returning with a null canvas.
                if (newUiCanvasObject != null && newUiCanvas != null && newUiRay != null) return;
                if (newUiCanvasObject != null) UnityEngine.Object.Destroy(newUiCanvasObject);
                newUiCanvasObject = null; newUiCanvas = null; newUiRay = null;
                stage = "creating canvas object";
                newUiCanvasObject = new GameObject("LunarLink Quantum UI Canvas");
                UnityEngine.Object.DontDestroyOnLoad(newUiCanvasObject);
                stage = "adding Canvas";
                newUiCanvas = newUiCanvasObject.AddComponent<Canvas>();
                if (newUiCanvas == null) throw new InvalidOperationException("AddComponent<Canvas>() returned null");
                newUiCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                newUiCanvas.sortingOrder = 31001;
                stage = "adding CanvasScaler";
                var scaler = newUiCanvasObject.AddComponent<CanvasScaler>();
                // These are Quantum Conveyors' exact CanvasScaler settings: it sets only
                // ScaleWithScreenSize and leaves the default 800x600 reference settings.
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                stage = "adding GraphicRaycaster";
                newUiRay = newUiCanvasObject.AddComponent<GraphicRaycaster>();
            }
            catch (Exception e)
            {
                Plugin.L.LogError("new UI canvas setup failed at " + stage + ": " + e);
                newUiCanvasObject = null; newUiCanvas = null; newUiRay = null;
            }
        }

        // The card is sized like Lunar Link's old window. The contents use Quantum's
        // own CanvasScaler and original 640x600/0.54 layout, so text and sprites render
        // at their native UI size instead of being enlarged after rendering.
        static void DrawNewUiPane()
        {
            EnsureNewUiCanvas();
            if (layer == null || newUiCanvas == null || newUiRay == null)
            {
                Plugin.L.LogError("new UI could not create its canvas");
                return;
            }
            ray = newUiRay;
            Canvas.ForceUpdateCanvases();
            var oldCanvas = layer.GetComponent<Canvas>();
            float oldScale = oldCanvas == null || oldCanvas.scaleFactor <= 0 ? 1f : oldCanvas.scaleFactor;
            float qcScale = newUiCanvas.scaleFactor <= 0 ? 1f : newUiCanvas.scaleFactor;
            float oldPaneH = Head + 190f + MaxRows * (RowH + Gap) + 30f;
            float paneW = W * oldScale / qcScale, paneH = oldPaneH * oldScale / qcScale;
            panel = Rect("New UI Pane", newUiCanvas.transform, new Vector2(paneW, paneH), Vector2.zero, Panel);
            panel.gameObject.AddComponent<CanvasGroup>().blocksRaycasts = true;
            float controlScale = oldScale / qcScale;
            UiVersionToggle(panel, paneW, paneH, controlScale);
            NewUiButton(panel, "×", new Vector2(paneW / 2 - 34 * controlScale, paneH / 2 - 36 * controlScale),
                new Vector2(52 * controlScale, 50 * controlScale), false, Close, Mathf.Max(8, Mathf.RoundToInt(34 * controlScale)));
            // the layout is 600 units tall (Quantum's own), scaled to fill the pane from its top edge (the title sits level
            // with the close cross) down to the band kept for the OLD UI button, and as wide as the pane at that scale
            float topBand = 8f * controlScale, bottomBand = 44f * controlScale;
            float layoutScale = (paneH - topBand - bottomBand) / 600f;
            float LW = Mathf.Max(640f, (paneW - 32f * controlScale) / layoutScale);
            newUiRowW = LW - 40f;
            var quantumLayout = Rect("Quantum Menu Layout", panel, new Vector2(LW, 600), new Vector2(0, (bottomBand - topBand) / 2f), null);
            quantumLayout.localScale = new Vector3(layoutScale, layoutScale, 1f);
            Label(quantumLayout, "Lunar Link", new Vector2(0, 278), new Vector2(LW - 20, 42), 26, Ink, TextAnchor.MiddleCenter);
            Help(quantumLayout, new Vector2(180, 278), "<b>Lunar Link</b> keeps this workspace open while we replace the old manager screen.\n\nThe folder and channel rows below are a visual starting point based on Quantum Conveyors.\n\nHot reload usually applies hot-module settings. Some settings are read only during game startup and still require a full restart.", new Vector2(560, 248), new Vector2(0, 40));
            const float tabGap = 10f; float tabWidth = Mathf.Min(170f, (LW - 40f - 3 * tabGap) / 4f);
            float tabStart = -(tabWidth * 4f + tabGap * 3f) / 2f + tabWidth / 2f;
            var linkTab = NewUiButton(quantumLayout, "Link Plugins", new Vector2(tabStart, 228), new Vector2(tabWidth, 40), newUiTab == 0,
                () => { newUiTab = 0; newUiSettingsPage = null; Draw(true); }, 14);
            var otherTab = NewUiButton(quantumLayout, "Other Plugins", new Vector2(tabStart + tabWidth + tabGap, 228), new Vector2(tabWidth, 40), newUiTab == 1,
                () => { newUiTab = 1; newUiSettingsPage = null; Draw(true); }, 14);
            var apiTab = NewUiButton(quantumLayout, "API", new Vector2(tabStart + (tabWidth + tabGap) * 2f, 228), new Vector2(tabWidth, 40), newUiTab == 2,
                () => { newUiTab = 2; newUiSettingsPage = null; Draw(true); }, 14);
            var updatesTab = NewUiButton(quantumLayout, "Updates", new Vector2(tabStart + (tabWidth + tabGap) * 3f, 228), new Vector2(tabWidth, 40), newUiTab == 3, null, 14);
            newUiTabImages[0] = linkTab.GetComponent<Image>(); newUiTabTexts[0] = linkTab.GetComponentInChildren<UnityEngine.UI.Text>();
            newUiTabImages[1] = otherTab.GetComponent<Image>(); newUiTabTexts[1] = otherTab.GetComponentInChildren<UnityEngine.UI.Text>();
            newUiTabImages[2] = apiTab.GetComponent<Image>(); newUiTabTexts[2] = apiTab.GetComponentInChildren<UnityEngine.UI.Text>();
            newUiTabImages[3] = updatesTab.GetComponent<Image>(); newUiTabTexts[3] = updatesTab.GetComponentInChildren<UnityEngine.UI.Text>();

            newUiViewport = Rect("Channel Viewport", quantumLayout, new Vector2(LW - 20, 446), new Vector2(0, -20.5f), Panel);
            newUiViewport.gameObject.AddComponent<Mask>().showMaskGraphic = true;
            newUiContent = Rect("Channel Content", newUiViewport, new Vector2(newUiRowW, 44), Vector2.zero, null);
            newUiContent.anchorMin = newUiContent.anchorMax = newUiContent.pivot = new Vector2(0.5f, 1f);
            newUiTransition = Rect("Channel Transition", newUiViewport, new Vector2(newUiRowW, 44), Vector2.zero, null);
            newUiTransition.anchorMin = newUiTransition.anchorMax = newUiTransition.pivot = new Vector2(0.5f, 1f);
            newUiTransition.SetSiblingIndex(newUiContent.GetSiblingIndex());
            newUiTransitionGroup = newUiTransition.gameObject.AddComponent<CanvasGroup>();
            newUiTransitionGroup.blocksRaycasts = false; newUiTransitionGroup.interactable = false;
            newUiTopFade = NewUiFade(newUiViewport, true, new Vector2(0, 217), LW - 20);
            newUiBottomFade = NewUiFade(newUiViewport, false, new Vector2(0, -217), LW - 20);

            // the modules switch and the menu key, side by side under the list: each a half-width box, its label on the
            // left and its control on the right
            float boxW = (LW - 50f) / 2f, boxY = -272f, labelW = boxW - 150f;
            var footer = Rect("Lunar Link Modules Panel", quantumLayout, new Vector2(boxW, 44), new Vector2(-(boxW + 10f) / 2f, boxY), Panel);
            newUiFooterObject = footer.gameObject;
            NewUiLabel(footer, "Lunar Link Modules", new Vector2(-boxW / 2 + 15 + labelW / 2, 0), new Vector2(labelW, 30), 18, Ink, TextAnchor.MiddleLeft);
            var moduleButton = NewUiButton(footer, (ShowModules?.Value ?? false) ? "Hide" : "Show", new Vector2(boxW / 2 - 70, 0), new Vector2(110, 34), ShowModules?.Value ?? false,
                () => { if (ShowModules != null) ShowModules.Value = !ShowModules.Value; SaveWindowSettings(); Draw(true); }, 16);
            newUiModuleButtonText = moduleButton.GetComponentInChildren<UnityEngine.UI.Text>();
            newUiModuleButtonImage = moduleButton.GetComponent<Image>();
            var keyRow = Rect("Lunar Link Menu Key Panel", quantumLayout, new Vector2(boxW, 44), new Vector2((boxW + 10f) / 2f, boxY), Panel);
            newUiKeyRowObject = keyRow.gameObject;
            NewUiLabel(keyRow, "Lunar Link Keybind", new Vector2(-boxW / 2 + 15 + labelW / 2, 0), new Vector2(labelW, 30), 18, Ink, TextAnchor.MiddleLeft);
            // Keep this as a focused key-capture field rather than adding Unity's
            // editable InputField component.  The menu already captures the next
            // keyboard control in CaptureNewUiKey(), and InputField's internal
            // navigation setup is not safe across LunarLink hot reloads.
            NewUiKeyField(keyRow, KeySetting?.Value ?? "F9", new Vector2(boxW / 2 - 70, 0), new Vector2(110, 34));
            newUiRestartButton = NewUiButton(quantumLayout, "Restart & Apply", new Vector2(-LW / 2 + 115, 278), new Vector2(190, 38), true, RestartNewUiGame, 16);
            newUiRestartButtonText = newUiRestartButton.GetComponentInChildren<UnityEngine.UI.Text>();
            newUiRestartButton.gameObject.SetActive(false);
            RefreshNewUiContent();
        }

        static void LoadNewUiData()
        {
            newUiGroups.Clear(); newUiChannels.Clear();
            try
            {
                var q = new System.Collections.Specialized.NameValueCollection();
                using (var d = System.Text.Json.JsonDocument.Parse(Hot.Http("GET", "/quantum/groups", q, null)))
                    newUiGroups.AddRange(d.RootElement.GetProperty("groups").EnumerateArray().Select(x => x.Clone()));
                using (var d = System.Text.Json.JsonDocument.Parse(Hot.Http("GET", "/quantum/channels", q, null)))
                    newUiChannels.AddRange(d.RootElement.GetProperty("channels").EnumerateArray().Select(x => x.Clone()));
                using (var d = System.Text.Json.JsonDocument.Parse(Hot.Http("GET", "/quantum", q, null)))
                    newUiAutoGroup = d.RootElement.TryGetProperty("autoGroup", out var auto) && auto.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            catch { }
        }

        static void RefreshNewUiContent()
        {
            if (newUiContent == null || newUiViewport == null) return;
            UpdateNewUiTabVisuals();
            if (newUiRestartButton != null)
            {
                bool restartRequired = newUiTab == 1
                    && (PluginSettings.Pending || BepPlugins.List().Any(e => e.State != null && e.State.Contains("next start")));
                newUiRestartButton.gameObject.SetActive(restartRequired);
            }
            bool showModules = ShowModules?.Value ?? false;
            if (newUiModuleButtonText != null)
            {
                newUiModuleButtonText.text = showModules ? "Hide" : "Show";
                newUiModuleButtonText.color = showModules ? Ink : Text;
            }
            if (newUiModuleButtonImage != null) newUiModuleButtonImage.color = showModules ? Accent : Btn;
            float previousScroll = newUiScroll;
            float previousTarget = newUiScrollTarget;
            bool animateTransition = newUiAnimateNext || newUiQueuedGradientRows.Count > 0;
            Dictionary<string, Vector2> previousPositions = null;
            Dictionary<string, RectTransform> previousRows = null;
            for (int i = newUiTransition.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(newUiTransition.GetChild(i).gameObject);
            newUiTransitionUntil = 0f; newUiTransitionFadeDuration = 0.22f; newUiTransitionRemoveOnly = false; newUiTransitionGroup.alpha = 1f;
            if (animateTransition)
            {
                previousPositions = new Dictionary<string, Vector2>(StringComparer.Ordinal);
                previousRows = new Dictionary<string, RectTransform>(StringComparer.Ordinal);
                foreach (var old in newUiLiveRows)
                {
                    if (old.Value == null) continue;
                    previousPositions[old.Key] = old.Value.anchoredPosition;
                    previousRows[old.Key] = old.Value;
                    old.Value.SetParent(newUiTransition, false);
                }
                newUiTransition.sizeDelta = newUiContent.sizeDelta;
                for (int i = newUiContent.childCount - 1; i >= 0; i--) newUiContent.GetChild(i).SetParent(newUiTransition, false);
                newUiTransition.anchoredPosition = newUiContent.anchoredPosition;
            }
            else
            {
                for (int i = newUiContent.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(newUiContent.GetChild(i).gameObject);
            }
            newUiLiveRows.Clear();
            newUiRowTweens.Clear(); newUiRow = 0;
            if (newUiSettingsPage != null) AddNewUiSettingsPage(newUiSettingsPage);
            else if (newUiTab == 1)
            {
                foreach (var e in BepPlugins.List()) AddNewUiBepPlugin(e);
            }
            else if (newUiTab == 0)
            {
                foreach (var r in Hot.Rows().Where(Show)) AddNewUiModule(r);
            }
            else if (newUiTab == 2)
            {
                foreach (var e in Endpoints()) AddNewUiApiEndpoint(e);
            }
            foreach (var row in newUiContent.GetComponentsInChildren<RectTransform>(true)) if (row.name == "Row") StretchNewUiRow(row);
            newUiContent.sizeDelta = new Vector2(newUiRowW, Math.Max(44f, newUiRow * 52f));
            float maxScroll = Mathf.Max(0f, newUiContent.rect.height - newUiViewport.rect.height);
            // opening a row scrolls the list on just far enough to show what opened (the rows below move down), but
            // never so far that the opened row itself goes above the top of the list
            float targetScroll = Mathf.Clamp(previousTarget, 0f, maxScroll);
            if (newUiOpened != null && previousPositions != null && newUiLiveRows.TryGetValue(newUiOpened, out var opened) && opened != null)
            {
                float Top(RectTransform r) { var tw = newUiRowTweens.FirstOrDefault(t => t.Row == r); return -(tw != null ? tw.To.y : r.anchoredPosition.y); }
                float parentTop = Top(opened), shownTo = -1f;
                foreach (var row in newUiLiveRows)
                    if (row.Value != null && !previousPositions.ContainsKey(row.Key) && Top(row.Value) > parentTop)
                        shownTo = Mathf.Max(shownTo, Top(row.Value) + row.Value.rect.height);   // the rows that just appeared
                if (shownTo > 0f)
                    targetScroll = Mathf.Clamp(Mathf.Max(targetScroll, Mathf.Min(shownTo - newUiViewport.rect.height, parentTop)), 0f, maxScroll);
            }
            newUiOpened = null;
            newUiScrollTarget = targetScroll;
            if (newUiAnimateNext && Mathf.Abs(previousScroll - targetScroll) > 0.01f)
            {
                newUiScrollAnimationFrom = previousScroll;
                newUiScrollAnimationTo = targetScroll;
                newUiScrollAnimationUntil = Time.unscaledTime + 0.22f;
                newUiScroll = previousScroll;
                newUiScrollVelocity = 0f;
            }
            else
            {
                newUiScrollAnimationUntil = 0f;
                newUiScroll = Mathf.Clamp(previousScroll, 0f, maxScroll);
                newUiScrollVelocity = 0f;
            }
            newUiContent.anchoredPosition = new Vector2(0, newUiScroll); UpdateNewUiFades();
            newUiTransition.anchoredPosition = new Vector2(0, newUiScroll);
            if (animateTransition)
            {
                foreach (var current in newUiLiveRows)
                {
                    var to = current.Value.anchoredPosition;
                    var from = newUiAnimateNext ? to + new Vector2(0, 16f) : to;
                    bool isNew = previousPositions == null || !previousPositions.ContainsKey(current.Key);
                    if (previousPositions != null && previousPositions.TryGetValue(current.Key, out var oldPosition)) from = oldPosition;
                    CanvasGroup fadeGroup = null;
                    bool gradientFade = newUiQueuedGradientRows.Contains(current.Key);
                    if (gradientFade)
                    {
                        var gradient = NewUiGradientImage(current.Value);
                        if (gradient != null)
                        {
                            var color = gradient.color; color.a = 0f; gradient.color = color;
                            var now = Time.unscaledTime;
                            if (newUiQueuedReloadRows.Contains(current.Key))
                            {
                                var greyStage = NewUiGreyStageImage(current.Value, gradient);
                                if (greyStage != null)
                                {
                                    newUiImageTweens.Add(new UiImageTween { Image = greyStage, From = 0f, To = 1f, Start = now + 0.11f, Until = now + 0.22f });
                                    newUiImageTweens.Add(new UiImageTween { Image = greyStage, From = 1f, To = 0f, Start = now + 0.22f, Until = now + 0.33f });
                                }
                                newUiImageTweens.Add(new UiImageTween { Image = gradient, From = 0f, To = 1f, Start = now + 0.33f, Until = now + 0.44f });
                            }
                            else
                            {
                                newUiImageTweens.Add(new UiImageTween { Image = gradient, From = 0f, To = 1f, Start = now + 0.11f, Until = now + 0.22f });
                            }
                        }
                    }
                    else if (isNew && (current.Key.EndsWith(":actions", StringComparison.Ordinal) || current.Key.Contains(":setting:", StringComparison.Ordinal)))
                    {
                        fadeGroup = current.Value.gameObject.AddComponent<CanvasGroup>();
                        fadeGroup.alpha = 0f;
                    }
                    if ((from - to).sqrMagnitude > 0.01f || fadeGroup != null)
                    {
                        current.Value.anchoredPosition = from;
                        float now = Time.unscaledTime;
                        float fadeStart = gradientFade ? now + 0.11f : now;
                        newUiRowTweens.Add(new UiRowTween { Row = current.Value, From = from, To = to, FadeGroup = fadeGroup, FadeFrom = 0f, FadeTo = 1f,
                            FadeStart = fadeStart, FadeUntil = now + 0.22f, Until = now + 0.22f });
                    }
                }
                bool hasClosingRows = false;
                if (previousRows != null)
                {
                    foreach (var old in previousRows)
                    {
                        if (newUiLiveRows.ContainsKey(old.Key))
                        {
                            if (newUiQueuedGradientRows.Contains(old.Key))
                            {
                                var gradient = NewUiGradientImage(old.Value);
                                if (gradient != null)
                                {
                                    var color = gradient.color; color.a = 1f; gradient.color = color;
                                    var now = Time.unscaledTime;
                                    newUiImageTweens.Add(new UiImageTween { Image = gradient, From = 1f, To = 0f, Start = now, Until = now + 0.11f });
                                }
                                hasClosingRows = true;
                                continue;
                            }
                            UnityEngine.Object.DestroyImmediate(old.Value.gameObject);
                            continue;
                        }
                        var target = old.Value.anchoredPosition;
                        if (old.Key.EndsWith(":actions", StringComparison.Ordinal) && newUiLiveRows.TryGetValue(old.Key.Substring(0, old.Key.Length - ":actions".Length), out var parentRow))
                            target = parentRow.anchoredPosition;
                        else if (old.Key.Contains(":setting:", StringComparison.Ordinal))
                        {
                            var cut = old.Key.IndexOf(":setting:", StringComparison.Ordinal);
                            if (cut > 0 && newUiLiveRows.TryGetValue(old.Key.Substring(0, cut), out parentRow)) target = parentRow.anchoredPosition;
                        }
                        if ((old.Value.anchoredPosition - target).sqrMagnitude > 0.01f)
                            newUiRowTweens.Add(new UiRowTween { Row = old.Value, From = old.Value.anchoredPosition, To = target, Until = Time.unscaledTime + 0.22f });
                        hasClosingRows = true;
                    }
                }
                if (hasClosingRows)
                {
                    if (!newUiAnimateNext && newUiQueuedGradientRows.Count > 0)
                    {
                        newUiTransitionRemoveOnly = true;
                        newUiTransitionUntil = Time.unscaledTime + 0.11f;
                    }
                    else
                    {
                        newUiTransitionFadeDuration = 0.22f;
                        newUiTransitionUntil = Time.unscaledTime + newUiTransitionFadeDuration;
                    }
                }
            }
            newUiAnimateNext = false; newUiQueuedGradientRows.Clear(); newUiQueuedReloadRows.Clear();
        }

        static void UpdateNewUiTabVisuals()
        {
            for (int i = 0; i < 4; i++)
            {
                bool selected = newUiTab == i;
                if (newUiTabImages[i] != null) newUiTabImages[i].color = selected ? Accent : Btn;
                if (newUiTabTexts[i] != null) newUiTabTexts[i].color = selected ? Ink : Text;
            }
            bool showFooter = newUiTab == 0;
            if (newUiFooterObject != null) newUiFooterObject.SetActive(showFooter);
            if (newUiKeyRowObject != null) newUiKeyRowObject.SetActive(showFooter);
        }

        static void AddNewUiModule(Hot.Row r)
        {
            bool disabled = r.State == "disabled";
            string display = DisplayNames.For(r.Name);
            string state = r.State == "running" || r.State == "disabled" ? "" : r.State;
            string detail = r.Error != null ? "<color=#E6524A>" + Short(r.Error) + "</color>"
                : (r.Newer ? "<color=#FF7A2E>newer build on disk</color>" : "")
                    + (r.NeedsRestart ? "   restart to register buildings" : "");
            bool open = newUiOpenModules.Contains(r.Name);
            string arrowKey = "m:" + r.Name;
            Action toggle = () =>
            {
                if (open)
                {
                    newUiOpenModules.Remove(r.Name);
                    newUiAnimateNext = true;
                }
                else
                {
                    foreach (var previous in newUiOpenModules.ToList())
                    {
                        newUiOpenModules.Remove(previous);
                        newUiAnimateNext = true;
                    }
                    newUiOpenModules.Add(r.Name);
                    newUiAnimateNext = true; newUiOpened = arrowKey;
                }
                Draw(true);
            };
            var rowKey = "m:" + r.Name;
            var row = NewUiRow(newUiContent, newUiRow++, 52f, false, disabled, false);
            newUiLiveRows[rowKey] = row;
            var rowButton = row.gameObject.AddComponent<UnityEngine.UI.Button>();
            rowButton.targetGraphic = row.GetComponent<Image>();
            actions[row.gameObject.GetInstanceID()] = toggle;
            var titleColor = r.State == "failed" ? Red : disabled ? new Color(Ink.r, Ink.g, Ink.b, 0.8f) : Ink;
            var title = NewUiLabel(row, display + (string.IsNullOrEmpty(state) ? "" : "  " + state),
                new Vector2(-120, string.IsNullOrEmpty(detail) ? 0 : 10), new Vector2(270, 24), 15, titleColor, TextAnchor.MiddleLeft);
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            if (!string.IsNullOrEmpty(detail))
            {
                var detailText = NewUiLabel(row, detail, new Vector2(-120, -12), new Vector2(300, 22), 13, Dim, TextAnchor.MiddleLeft);
                detailText.supportRichText = true;
            }
            NewUiButton(row, open ? "-" : "+", new Vector2(264, 0), new Vector2(48, 34), true, toggle, 22);     // + opens, - closes
            if (!open) return;

            var actionsRow = NewUiRow(newUiContent, newUiRow++, 52f, true, false, false);
            // Keep the expanding dropdown behind its module header.  The
            // collapse transition already lives in the lower transition layer;
            // matching that sibling order prevents the new row from drawing over
            // the header while it slides into place.
            actionsRow.SetSiblingIndex(row.GetSiblingIndex());
            newUiLiveRows[rowKey + ":actions"] = actionsRow;
            var version = ModuleVersion(r.Name);
            NewUiLabel(actionsRow, version != null ? "version " + version : disabled ? "Enable this module" : "Module actions",
                new Vector2(-120, 0), new Vector2(230, 34), 14, Dim, TextAnchor.MiddleLeft);
            string moduleName = r.Name;
            const float arrowCenter = 264f, arrowWidth = 48f, buttonGap = 8f, disableWidth = 86f, reloadWidth = 84f;
            const float actionRight = arrowCenter + arrowWidth / 2f;
            float reloadX = actionRight - reloadWidth / 2f;
            float disableX = actionRight - reloadWidth - buttonGap - disableWidth / 2f;
            if (PluginSettings.For(moduleName, null, moduleName + ".dll").Count > 0)
                NewUiButton(actionsRow, "Settings", new Vector2(disableX - disableWidth / 2f - buttonGap - SettingsButtonW / 2f, 0), new Vector2(SettingsButtonW, 34), false,
                    () => OpenNewUiSettings(new SettingsPage { Title = display, Name = moduleName, File = moduleName + ".dll", Hot = true }), 16);
            NewUiButton(actionsRow, disabled ? "Enable" : "Disable", new Vector2(disableX, 0), new Vector2(disableWidth, 34), disabled,
                () => Act(() =>
                {
                    QueueNewUiGradient(moduleName, false);
                    var result = disabled ? Hot.Enable(moduleName, reload: false) : Hot.Disable(moduleName);
                    return Done(result, disabled ? "Enabled " + display : "Disabled " + display);
                }), 16);
            var reloadButton = NewUiButton(actionsRow, "Reload", new Vector2(reloadX, 0), new Vector2(reloadWidth, 34), false,
                () => Act(() =>
                {
                    QueueNewUiGradient(moduleName, true);
                    return Done(Hot.Enable(moduleName, reload: true), "Reloaded " + display);
                }), 16, !disabled);
            if (disabled)
            {
                var reloadControl = reloadButton.GetComponent<UnityEngine.UI.Button>();
                if (reloadControl != null) reloadControl.interactable = false;
                var disabledGroup = reloadButton.gameObject.AddComponent<CanvasGroup>();
                disabledGroup.alpha = 0.45f; disabledGroup.interactable = false; disabledGroup.blocksRaycasts = false;
                actions.Remove(reloadButton.gameObject.GetInstanceID());
            }
        }

        const float SettingsButtonW = 96f;

        static void OpenNewUiSettings(SettingsPage page)
        {
            page.ScrollBefore = newUiScrollTarget;
            newUiSettingsPage = page;
            newUiScroll = newUiScrollTarget = 0f; newUiAnimateNext = true;
            Draw(true);
        }

        static void CloseNewUiSettings()
        {
            var page = newUiSettingsPage; newUiSettingsPage = null;
            if (page != null) { newUiScroll = newUiScrollTarget = page.ScrollBefore; }
            newUiAnimateNext = true;
            Draw(true);
        }

        // The settings page: a header row (Back, the plugin's name, and for a hot plugin Reload, which applies the changes
        // at once: a reload reads its config again), then its settings, under a heading for each section when it has more
        // than one. A normal BepInEx plugin's changes wait for a restart (Restart & Apply).
        static void AddNewUiSettingsPage(SettingsPage page)
        {
            var settings = PluginSettings.For(page.Name, page.Guid, page.File);
            var header = NewUiRow(newUiContent, newUiRow++, 52f, true);
            newUiLiveRows["s:header"] = header;
            NewUiButton(header, "‹ Back", new Vector2(-250, 0), new Vector2(90, 34), false, CloseNewUiSettings, 16);
            NewUiLabel(header, page.Title + "  <size=13><color=#77736E>settings</color></size>", new Vector2(-40, 0), new Vector2(300, 34), 16, Ink, TextAnchor.MiddleLeft).supportRichText = true;
            if (page.Hot)
            {
                bool changed = settings.Any(PluginSettings.Changed);
                NewUiButton(header, "Reload", new Vector2(246, 0), new Vector2(84, 34), changed, () => Act(() =>
                {
                    var result = Hot.Enable(page.Name, reload: true);
                    if (result != null && result.Error == null) PluginSettings.Applied(settings);
                    return Done(result, "Reloaded " + page.Title + (changed ? ": its new settings are in use" : ""));
                }), 16);
            }
            bool sections = settings.Select(x => x.Section).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            string section = null; int index = 0;
            foreach (var setting in settings)
            {
                if (sections && !string.Equals(section, setting.Section, StringComparison.OrdinalIgnoreCase))
                {
                    section = setting.Section;
                    var heading = NewUiRow(newUiContent, newUiRow++, 52f, true, true);
                    newUiLiveRows["s:section:" + section] = heading;
                    NewUiLabel(heading, "<b>" + section + "</b>", new Vector2(-120, 0), new Vector2(300, 30), 14, Dim, TextAnchor.MiddleLeft).supportRichText = true;
                }
                var row = NewUiRow(newUiContent, newUiRow++, 52f, false);
                newUiLiveRows["s:" + index++] = row;
                AddNewUiSetting(row, setting);
            }
            if (settings.Count == 0)
            {
                var empty = NewUiRow(newUiContent, newUiRow++, 52f, true, true);
                newUiLiveRows["s:none"] = empty;
                NewUiLabel(empty, "No settings found in its config file", new Vector2(-120, 0), new Vector2(300, 30), 14, Dim, TextAnchor.MiddleLeft);
            }
        }

        static void AddNewUiSetting(RectTransform row, PluginSettings.Entry setting)
        {
            var title = NewUiLabel(row, setting.Key, new Vector2(-120, string.IsNullOrEmpty(setting.Description) ? 0 : 10),
                new Vector2(300, 24), 14, Ink, TextAnchor.MiddleLeft);
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            if (!string.IsNullOrEmpty(setting.Description))
            {
                var description = NewUiLabel(row, ShortSetting(setting.Description), new Vector2(-120, -12), new Vector2(320, 20), 11, Dim, TextAnchor.MiddleLeft);
                description.horizontalOverflow = HorizontalWrapMode.Overflow;
            }

            var value = SettingDisplayValue(setting);
            const float settingWidth = 150f;
            const float arrowRight = 264f + 48f / 2f;
            var button = NewUiButton(row, value, new Vector2(arrowRight - settingWidth / 2f, 0), new Vector2(settingWidth, 34), false,
                () => ChangeNewUiSetting(setting), 13);
            var text = button.GetComponentInChildren<UnityEngine.UI.Text>();
            if (newUiCapturingSetting && newUiEditingSetting != null && newUiEditingSetting.Id == setting.Id)
            {
                newUiSettingInputText = text;
                UpdateNewUiSettingInput();
            }
        }

        static string ShortSetting(string text)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 54 ? text.Substring(0, 54) + "…" : text;
        }

        static string SettingDisplayValue(PluginSettings.Entry setting)
        {
            if (newUiCapturingSetting && newUiEditingSetting != null && newUiEditingSetting.Id == setting.Id)
                return (newUiSettingDraft ?? "") + "  |  Enter = save";
            return setting.Type == PluginSettings.Kind.Toggle
                ? (setting.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "On" : "Off")
                : (setting.Value ?? "").Length > 22 ? setting.Value.Substring(0, 22) + "…" : (setting.Value ?? "");
        }

        static void ChangeNewUiSetting(PluginSettings.Entry setting)
        {
            if (setting.Type == PluginSettings.Kind.Toggle)
            {
                Act(() => PluginSettings.Toggle(setting));
                return;
            }
            if (setting.Type == PluginSettings.Kind.Choice)
            {
                Act(() => PluginSettings.Next(setting));
                return;
            }
            BeginNewUiSettingEdit(setting);
        }

        // Other Plugins uses the same row/dropdown renderer as Link Plugins.
        // Its contents and actions intentionally come from the old BepInEx
        // plugins tab, so the new tab changes presentation only.
        static void AddNewUiBepPlugin(BepPlugins.Entry e)
        {
            bool disabled = e.State == "off" || e.State == "off from next start";
            bool ordinary = e.State == "running" || e.State == "off" || e.State == "off from next start";
            string display = DisplayNames.For(e.Name);
            string state = ordinary || e.State == "on from next start" ? "" : e.State;
            string detail = e.NeededBy.Count > 0 ? "needed by " + string.Join(", ", e.NeededBy.Select(DisplayNames.For)) : "";

            string key = e.File;
            string arrowKey = "b:" + key;
            bool open = newUiOpenBepPlugins.Contains(key);
            Action toggle = () =>
            {
                if (open)
                {
                    newUiOpenBepPlugins.Remove(key);
                    newUiAnimateNext = true;
                }
                else
                {
                    foreach (var previous in newUiOpenBepPlugins.ToList())
                    {
                        newUiOpenBepPlugins.Remove(previous);
                        newUiAnimateNext = true;
                    }
                    newUiOpenBepPlugins.Add(key);
                    newUiAnimateNext = true; newUiOpened = arrowKey;
                }
                Draw(true);
            };

            var rowKey = "b:" + key;
            var row = NewUiRow(newUiContent, newUiRow++, 52f, false, disabled, false);
            newUiLiveRows[rowKey] = row;
            var rowButton = row.gameObject.AddComponent<UnityEngine.UI.Button>();
            rowButton.targetGraphic = row.GetComponent<Image>();
            actions[row.gameObject.GetInstanceID()] = toggle;

            var titleColor = e.State == "file gone" ? Red : disabled ? new Color(Ink.r, Ink.g, Ink.b, 0.8f) : Ink;
            var title = NewUiLabel(row, display + (e.Version == null ? "" : "  " + e.Version) + (string.IsNullOrEmpty(state) ? "" : "  " + state)
                    + (e.Self ? "   (runs this window)" : ""),
                new Vector2(-120, string.IsNullOrEmpty(detail) ? 0 : 10), new Vector2(270, 24), 15, titleColor, TextAnchor.MiddleLeft);
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            if (!string.IsNullOrEmpty(detail))
            {
                var detailText = NewUiLabel(row, detail, new Vector2(-120, -12), new Vector2(400, 22), 13, Dim, TextAnchor.MiddleLeft);
                detailText.supportRichText = true;
            }

            NewUiButton(row, open ? "-" : "+", new Vector2(264, 0), new Vector2(48, 34), true, toggle, 22);     // + opens, - closes
            if (!open) return;

            var actionsRow = NewUiRow(newUiContent, newUiRow++, 52f, true, false, false);
            actionsRow.SetSiblingIndex(row.GetSiblingIndex());
            newUiLiveRows[rowKey + ":actions"] = actionsRow;
            var fileLabel = NewUiLabel(actionsRow, e.File, new Vector2(-120, 0), new Vector2(230, 34), 14, Dim, TextAnchor.MiddleLeft);
            fileLabel.horizontalOverflow = HorizontalWrapMode.Overflow;

            const float arrowCenter = 264f, arrowWidth = 48f, disableWidth = 86f;
            const float actionRight = arrowCenter + arrowWidth / 2f;
            float disableX = actionRight - disableWidth / 2f;
            if (!e.Self && e.State == "running")
            {
                string file = e.File;
                NewUiButton(actionsRow, "Disable", new Vector2(disableX, 0), new Vector2(disableWidth, 34), false,
                    () => Act(() =>
                    {
                        QueueNewUiGradientKey(rowKey, false);
                        return BepPlugins.Disable(file) ?? "Turned off: " + file + ". RESTART THE GAME to apply (it keeps running until then)";
                    }), 16);
            }
            else if (!e.Self && (e.State == "off" || e.State == "off from next start"))
            {
                string file = e.File;
                NewUiButton(actionsRow, "Enable", new Vector2(disableX, 0), new Vector2(disableWidth, 34), true,
                    () => Act(() =>
                    {
                        QueueNewUiGradientKey(rowKey, false);
                        return BepPlugins.Enable(file) ?? (e.Loaded ? "Turned back on: " + file + ". It never stopped, so no restart is needed" : "Turned on: " + file + ". RESTART THE GAME to apply");
                    }), 16);
            }
            else if (!e.Self && e.State == "on from next start")
            {
                string file = e.File;
                NewUiButton(actionsRow, "Disable", new Vector2(disableX, 0), new Vector2(disableWidth, 34), false,
                    () => Act(() =>
                    {
                        QueueNewUiGradientKey(rowKey, false);
                        return BepPlugins.Disable(file) ?? "Turned back off: " + file + ". It never loaded, so no restart is needed";
                    }), 16);
            }
            if (PluginSettings.For(e.Name, e.Guid, e.File).Count > 0)
            {
                bool hasToggle = !e.Self && e.State != "file gone";
                float settingsX = hasToggle ? disableX - disableWidth / 2f - 8f - SettingsButtonW / 2f : actionRight - SettingsButtonW / 2f;
                string name = e.Name, guid = e.Guid, file = e.File;
                NewUiButton(actionsRow, "Settings", new Vector2(settingsX, 0), new Vector2(SettingsButtonW, 34), false,
                    () => OpenNewUiSettings(new SettingsPage { Title = display, Name = name, Guid = guid, File = file, Hot = false }), 16);
            }
        }

        static void AddNewUiApiEndpoint((string group, string from, string method, string path, string help) e)
        {
            string key = e.group + "|" + e.method + "|" + e.path;
            string rowKey = "a:" + key;
            bool open = newUiOpenApiEndpoints.Contains(key);
            Action toggle = () =>
            {
                if (open)
                {
                    newUiOpenApiEndpoints.Remove(key);
                    newUiAnimateNext = true;
                }
                else
                {
                    foreach (var previous in newUiOpenApiEndpoints.ToList())
                    {
                        newUiOpenApiEndpoints.Remove(previous);
                        newUiAnimateNext = true;
                    }
                    newUiOpenApiEndpoints.Add(key);
                    newUiAnimateNext = true; newUiOpened = rowKey;
                }
                Draw(true);
            };

            var row = NewUiRow(newUiContent, newUiRow++, 52f, false, false, false);
            newUiLiveRows[rowKey] = row;
            var rowButton = row.gameObject.AddComponent<UnityEngine.UI.Button>();
            rowButton.targetGraphic = row.GetComponent<Image>();
            actions[row.gameObject.GetInstanceID()] = toggle;

            var titleColor = e.from == "core" ? Ink : e.from.Contains("plugin") ? Accent : Green;
            var title = NewUiLabel(row, e.method + " " + e.path, new Vector2(-120, string.IsNullOrEmpty(e.group) ? 0 : 10),
                new Vector2(270, 24), 15, titleColor, TextAnchor.MiddleLeft);
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            if (!string.IsNullOrEmpty(e.group))
                NewUiLabel(row, e.group, new Vector2(-120, -12), new Vector2(300, 22), 13, Dim, TextAnchor.MiddleLeft);

            NewUiButton(row, open ? "-" : "+", new Vector2(264, 0), new Vector2(48, 34), true, toggle, 22);     // + opens, - closes
            if (!open) return;

            var actionsRow = NewUiRow(newUiContent, newUiRow++, 52f, true, false, false);
            actionsRow.SetSiblingIndex(row.GetSiblingIndex());
            newUiLiveRows[rowKey + ":actions"] = actionsRow;
            var help = NewUiLabel(actionsRow, e.help ?? "No help text supplied", new Vector2(-120, 0), new Vector2(470, 34), 13, Dim, TextAnchor.MiddleLeft);
            help.horizontalOverflow = HorizontalWrapMode.Overflow;
            help.supportRichText = true;
        }

        static void QueueNewUiGradient(string moduleName, bool reload)
        {
            QueueNewUiGradientKey("m:" + moduleName, reload);
        }

        static void QueueNewUiGradientKey(string key, bool reload)
        {
            newUiQueuedGradientRows.Add(key);
            if (reload) newUiQueuedReloadRows.Add(key);
        }

        static string ModuleVersion(string name)
        {
            try
            {
                var loaded = Hot.Loaded().FirstOrDefault(x => string.Equals(x.name, name, StringComparison.OrdinalIgnoreCase)).obj;
                var informational = loaded?.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                var version = NormalizeModuleVersion(informational);
                if (version != null) return version;

                var file = Path.Combine(BepInEx.Paths.BepInExRootPath, "hot", name + ".dll");
                var info = FileVersionInfo.GetVersionInfo(file);
                version = NormalizeModuleVersion(info.ProductVersion);
                if (version != null) return version;
                return NormalizeModuleVersion(info.FileVersion);
            }
            catch { return null; }
        }

        static string NormalizeModuleVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var value = raw.Trim();
            var cut = value.IndexOfAny(new[] { '+', ' ', '(' });
            if (cut > 0) value = value.Substring(0, cut).Trim();
            if (!System.Version.TryParse(value, out var parsed)) return null;
            return parsed.ToString(3);
        }


        static void BeginNewUiKeyCapture()
        {
            newUiCapturingKey = true;
            UpdateNewUiKeyField();
        }

        static void CaptureNewUiKey(Keyboard kb)
        {
            if (kb.escapeKey.wasPressedThisFrame)
            {
                newUiCapturingKey = false;
                UpdateNewUiKeyField();
                return;
            }
            foreach (Key candidate in Enum.GetValues(typeof(Key)))
            {
                if (candidate == Key.None) continue;
                try
                {
                    var control = kb.get_Item(candidate);
                    if (control == null || !control.wasPressedThisFrame) continue;
                    if (KeySetting != null) KeySetting.Value = candidate.ToString();
                    SaveWindowSettings();
                    keyName = null;
                    newUiCapturingKey = false;
                    UpdateNewUiKeyField();
                    return;
                }
                catch { }
            }
        }

        static void UpdateNewUiKeyField()
        {
            if (newUiKeyInputText != null) newUiKeyInputText.text = newUiCapturingKey ? "Press a key" : (KeySetting?.Value ?? "F9");
            if (newUiKeyInputImage != null) newUiKeyInputImage.color = newUiCapturingKey ? Accent : Btn;
        }

        static void BeginNewUiSettingEdit(PluginSettings.Entry setting)
        {
            if (setting == null) return;
            newUiEditingSetting = setting;
            newUiSettingDraft = setting.Value ?? "";
            newUiCapturingSetting = true;
            message = "Type a value for " + setting.Key + ". Enter saves; Escape cancels.";
            Draw(true);
        }

        static void CaptureNewUiSetting(Keyboard kb)
        {
            if (newUiEditingSetting == null) { newUiCapturingSetting = false; return; }
            foreach (var candidate in Enum.GetValues(typeof(Key)))
            {
                var keyValue = (Key)candidate;
                var name = keyValue.ToString();
                var control = kb.get_Item(keyValue);
                if (control == null || !control.wasPressedThisFrame) continue;
                if (name.Equals("Escape", StringComparison.OrdinalIgnoreCase))
                {
                    newUiCapturingSetting = false; newUiEditingSetting = null; newUiSettingDraft = null; Draw(true); return;
                }
                if (name.Equals("Enter", StringComparison.OrdinalIgnoreCase) || name.Equals("NumpadEnter", StringComparison.OrdinalIgnoreCase))
                {
                    var result = PluginSettings.Set(newUiEditingSetting, newUiSettingDraft);
                    message = result;
                    newUiCapturingSetting = false; newUiEditingSetting = null; newUiSettingDraft = null; Draw(true); return;
                }
                if (name.Equals("Backspace", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(newUiSettingDraft)) newUiSettingDraft = newUiSettingDraft.Substring(0, newUiSettingDraft.Length - 1);
                    UpdateNewUiSettingInput(); return;
                }
                var typed = TypedSettingCharacter(name, kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
                if (typed.HasValue && newUiSettingDraft.Length < 120)
                {
                    newUiSettingDraft += typed.Value;
                    UpdateNewUiSettingInput();
                }
                return;
            }
        }

        static char? TypedSettingCharacter(string key, bool shift)
        {
            if (key.Length == 1 && char.IsLetter(key[0])) return shift ? char.ToUpperInvariant(key[0]) : char.ToLowerInvariant(key[0]);
            if (key.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase) && key.Length == 6 && char.IsDigit(key[5])) return key[5];
            if (key.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) && key.Length == 7 && char.IsDigit(key[6])) return key[6];
            switch (key)
            {
                case "Space": return ' ';
                case "Period": return '.';
                case "Comma": return ',';
                case "Minus": return '-';
                case "Equals": return shift ? '+' : '=';
                case "Slash": return '/';
                case "Backslash": return '\\';
                case "Semicolon": return shift ? ':' : ';';
                case "Quote": return shift ? '"' : '\'';
                case "LeftBracket": return shift ? '{' : '[';
                case "RightBracket": return shift ? '}' : ']';
                case "Backquote": return shift ? '~' : '`';
                default: return null;
            }
        }

        static void UpdateNewUiSettingInput()
        {
            if (newUiSettingInputText != null) newUiSettingInputText.text = newUiCapturingSetting ? (newUiSettingDraft ?? "") + "  |  Enter = save" : newUiSettingDraft;
        }

        static void SaveWindowSettings()
        {
            try { ShowModules?.ConfigFile?.Save(); }
            catch (Exception e) { Plugin.L.LogWarning("window settings save skipped: " + e.Message); }
        }

        static bool BeginNativeGameSave(out string reason)
        {
            reason = null;
            try
            {
                var type = typeof(MoonCleaners.MoonPauseMenu);
                var objects = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(type));
                if (objects == null || objects.Length == 0)
                {
                    // At the title screen there is no active world to save.
                    if (Api.World == null) return true;
                    reason = "Moon Sweepers' save menu is not available";
                    return false;
                }
                var menu = Activator.CreateInstance(type, new object[] { objects[0].Pointer });
                var save = type.GetMethod("SaveGame", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (save == null) { reason = "Moon Sweepers' native save method was not found"; return false; }
                save.Invoke(menu, null);
                return true;
            }
            catch (Exception e)
            {
                var cause = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                reason = cause.GetType().Name + ": " + cause.Message;
                return false;
            }
        }

        static void RestartNewUiGame()
        {
            if (newUiRestartWaiting) return;
            if (!BeginNativeGameSave(out var reason))
            {
                message = "Restart cancelled: " + reason + ". Restart the game manually if needed.";
                Plugin.L.LogWarning("window: native save was not started: " + reason);
                Draw(true);
                return;
            }
            newUiRestartWaiting = true;
            newUiRestartQuitAt = Time.unscaledTime + 3f;
            if (newUiRestartButtonText != null) newUiRestartButtonText.text = "Saving...";
            if (newUiRestartButton != null) actions.Remove(newUiRestartButton.gameObject.GetInstanceID());
            Plugin.L.LogInfo("window: native game save requested; waiting before restart");
        }

        static void FinishNewUiRestart()
        {
            newUiRestartWaiting = false;
            // The game is distributed through Steam.  Match the installed Mod
            // Manager's restart approach: schedule a hidden helper to wait for
            // this process to exit, then relaunch the same Steam app.
            const string steamAppId = "5335510"; // Moon Sweepers' Steam app id
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                string script = "$p=Get-Process -Id " + pid + " -ErrorAction SilentlyContinue; if ($p) { Wait-Process -Id " + pid + " -ErrorAction SilentlyContinue }; Start-Sleep -Seconds 1; Start-Process 'steam://rungameid/" + steamAppId + "'";
                var start = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-WindowStyle");
                start.ArgumentList.Add("Hidden");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add(script);
                Process.Start(start);
                Plugin.L.LogInfo("window: restarting Moon Sweepers through Steam to apply plugin changes");
                UnityEngine.Application.Quit();
            }
            catch (Exception e)
            {
                message = "Could not schedule the restart: " + e.Message + ". Restart the game manually.";
                Plugin.L.LogWarning("window: restart scheduling failed: " + e);
                Draw(true);
            }
        }

        static void AddNewUiFolder(string label, string count, int depth, Action toggle, bool groupActions, bool expanded, string arrowKey)
        {
            var r = NewUiRow(newUiContent, newUiRow++, 52f, true); NewUiFolder(r, depth);
            var rowButton = r.gameObject.AddComponent<UnityEngine.UI.Button>();
            rowButton.targetGraphic = r.GetComponent<Image>();
            actions[r.gameObject.GetInstanceID()] = toggle;
            float x = groupActions ? -95 + depth * 14 : -23.5f, width = groupActions ? 320 - depth * 14 : 467;
            NewUiLabel(r, label + "  <size=13><color=#77736E>" + count + "</color></size>", new Vector2(x, 0), new Vector2(width, 40), 15, Ink, TextAnchor.MiddleLeft).supportRichText = true;
            if (groupActions) { NewUiButton(r, "Rename", new Vector2(115, 0), new Vector2(86, 34), false); NewUiButton(r, "Delete", new Vector2(205, 0), new Vector2(84, 34), false); }
            NewUiButton(r, expanded ? "-" : "+", new Vector2(276, 0), new Vector2(48, 34), true, toggle, 22);     // + opens, - closes
        }

        static void RenderNewUiGroup(System.Text.Json.JsonElement group, int depth)
        {
            string id = group.GetProperty("id").GetString(); bool open = newUiOpenGroups.Contains(id);
            string count = UiCountPhrase(group.GetProperty("channels").GetInt32(), "channel") + ", " + UiCountPhrase(group.GetProperty("adapters").GetInt32(), "adapter");
            AddNewUiFolder(group.GetProperty("name").GetString(), count, depth, () => { if (open) newUiOpenGroups.Remove(id); else newUiOpenGroups.Add(id); newUiAnimateNext = true; if (!open) newUiOpened = "g:" + id; Draw(true); }, true, open, "g:" + id);
            if (!open) return;
            foreach (var child in newUiGroups.Where(g => g.TryGetProperty("parent", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String && p.GetString() == id)) RenderNewUiGroup(child, depth + 1);
            foreach (var channel in newUiChannels.Where(c => c.TryGetProperty("group", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.String && g.GetString() == id)) RenderNewUiChannel(channel, depth + 1, false);
        }

        static void RenderNewUiChannel(System.Text.Json.JsonElement channel, int depth, bool fullName)
        {
            var r = NewUiRow(newUiContent, newUiRow++, 52f, false);
            string name = fullName || depth == 0 ? channel.GetProperty("name").GetString() : channel.GetProperty("short").GetString();
            NewUiLabel(r, name + "  <size=13><color=#77736E>(" + channel.GetProperty("outputs").GetInt32() + " in / " + channel.GetProperty("inputs").GetInt32() + " out)</color></size>", new Vector2(-125 + depth * 14, 0), new Vector2(260 - depth * 14, 40), 15, Ink, TextAnchor.MiddleLeft).supportRichText = true;
            NewUiButton(r, "Assign", new Vector2(22, 0), new Vector2(86, 34), false); NewUiButton(r, "Rename", new Vector2(114, 0), new Vector2(88, 34), false); NewUiButton(r, "Delete", new Vector2(205, 0), new Vector2(84, 34), false);
        }

        static string UiCountPhrase(int count, string singular) => count + " " + (count == 1 ? singular : singular + "s");

        static RectTransform NewUiRow(Transform parent, int index, float rowH, bool folder, bool muted = false, bool animate = true)
        {
            EnsureSprites();
            var row = Rect("Row", parent, new Vector2(600, 46), new Vector2(0, -index * rowH), Panel);
            var image = row.GetComponent<Image>(); image.sprite = rounded; image.type = Image.Type.Sliced;
            var shade = Rect("Row Gradient", row, new Vector2(600, 46), Vector2.zero, null);
            var g = shade.gameObject.AddComponent<Image>(); g.sprite = muted ? grey : folder ? cool : warm; g.type = Image.Type.Sliced; g.raycastTarget = false;
            shade.SetAsFirstSibling();
            row.anchorMin = row.anchorMax = row.pivot = new Vector2(0.5f, 1f); row.anchoredPosition = new Vector2(0, -index * rowH);
            if (animate && newUiAnimateNext)
            {
                var to = row.anchoredPosition; var from = to + new Vector2(0, 16);
                row.anchoredPosition = from;
                newUiRowTweens.Add(new UiRowTween { Row = row, From = from, To = to, Until = Time.unscaledTime + 0.22f });
            }
            return row;
        }

        // rows are laid out 600 wide (Quantum's size); this widens one to the list's width: what sits on the right keeps
        // its distance from the right edge, what sits on the left from the left edge, left-aligned text grows into the
        // room between, and anything centred spans the new width
        static float newUiRowW = 600f;
        static void StretchNewUiRow(RectTransform row)
        {
            float dx = (newUiRowW - 600f) / 2f;
            if (dx <= 0f || Mathf.Abs(row.sizeDelta.x - 600f) > 0.5f) return;              // already stretched
            for (int i = 0; i < row.childCount; i++)
            {
                var c = row.GetChild(i) as RectTransform; if (c == null || c.name == "Row") continue;
                var p = c.anchoredPosition; var text = c.GetComponent<UnityEngine.UI.Text>();
                bool leftText = text != null && (text.alignment == TextAnchor.MiddleLeft || text.alignment == TextAnchor.UpperLeft || text.alignment == TextAnchor.LowerLeft);
                if (Mathf.Abs(p.x) < 1f || leftText && p.x < 0f) c.sizeDelta = new Vector2(c.sizeDelta.x + 2f * dx, c.sizeDelta.y);
                else c.anchoredPosition = new Vector2(p.x + (p.x > 0f ? dx : -dx), p.y);
            }
            row.sizeDelta = new Vector2(newUiRowW, row.sizeDelta.y);
        }

        static Image NewUiGradientImage(RectTransform row)
        {
            return row == null ? null : row.Find("Row Gradient")?.GetComponent<Image>();
        }

        static Image NewUiGreyStageImage(RectTransform row, Image gradient)
        {
            if (row == null || gradient == null) return null;
            var overlay = Rect("Reload Grey Stage", row, row.sizeDelta, Vector2.zero, null);
            overlay.SetSiblingIndex(gradient.transform.GetSiblingIndex() + 1);
            var image = overlay.gameObject.AddComponent<Image>();
            image.sprite = grey; image.type = Image.Type.Sliced; image.raycastTarget = false;
            image.color = new Color(1f, 1f, 1f, 0f);
            return image;
        }

        static void NewUiFolder(Transform parent, int depth)
        {
            EnsureSprites();
            var r = Rect("Folder Icon", parent, new Vector2(22, 22), new Vector2(-270 + depth * 14, 0), null);
            var image = r.gameObject.AddComponent<Image>(); image.sprite = newUiFolderIcon; image.preserveAspect = true; image.raycastTarget = false;
        }

        static UnityEngine.UI.Text NewUiLabel(Transform parent, string value, Vector2 pos, Vector2 size, int fontSize, Color color, TextAnchor align)
        {
            var text = Label(parent, value, pos, size, fontSize, color, align);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return text;
        }

        static RectTransform NewUiButton(Transform parent, string label, Vector2 pos, Vector2 size, bool accent, Action action = null, int fontSize = 16, bool interactable = true)
        {
            bool close = label == "×";
            var rect = Rect("Quantum Style Button", parent, size, pos, close ? new Color(0, 0, 0, 0) : accent ? Accent : Btn);
            Round(rect.GetComponent<Image>(), button);
            var b = rect.gameObject.AddComponent<UnityEngine.UI.Button>(); b.targetGraphic = rect.GetComponent<Image>(); b.interactable = interactable;
            actions[rect.gameObject.GetInstanceID()] = action ?? (() => { });
            NewUiLabel(rect, label, Vector2.zero, size - new Vector2(8, 4), fontSize, close ? Faint : accent ? Ink : Text, TextAnchor.MiddleCenter);
            return rect;
        }

        static RectTransform NewUiKeyField(Transform parent, string value, Vector2 pos, Vector2 size)
        {
            var rect = Rect("Lunar Link Key Input", parent, size, pos, Btn);
            var image = rect.GetComponent<Image>();
            Round(image, button);
            var buttonComponent = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
            buttonComponent.targetGraphic = image;
            actions[rect.gameObject.GetInstanceID()] = BeginNewUiKeyCapture;
            newUiKeyInputText = NewUiLabel(rect, value, Vector2.zero, size - new Vector2(8, 4), 16, Text, TextAnchor.MiddleCenter);
            newUiKeyInputImage = image;
            return rect;
        }

        static void ScrollNewUi(float wheel)
        {
            if (newUiContent == null || newUiViewport == null) return;
            float max = Mathf.Max(0, newUiContent.rect.height - newUiViewport.rect.height);
            if (max <= 0) return;
            if (newUiScrollAnimationUntil > 0f)
            {
                newUiScrollAnimationUntil = 0f;
                newUiScrollTarget = newUiScroll;
                newUiScrollVelocity = 0f;
            }
            newUiScrollTarget = Mathf.Clamp(newUiScrollTarget - (Mathf.Abs(wheel) > 10f ? wheel / 6f : wheel * 30f), 0, max);
        }

        static Image NewUiFade(Transform parent, bool top, Vector2 pos, float width)
        {
            const int h = 64; var texture = new Texture2D(1, h, TextureFormat.RGBA32, false) { name = top ? "Quantum Top Fade" : "Quantum Bottom Fade", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[h];
            for (int y = 0; y < h; y++) { float t = y / (float)(h - 1); pixels[y] = new Color(1, 1, 1, top ? t : 1f - t); }
            texture.SetPixels(pixels); texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new UnityEngine.Rect(0, 0, 1, h), new Vector2(0.5f, 0.5f), 100f);
            if (top) newUiTopFadeSprite = sprite; else newUiBottomFadeSprite = sprite;
            var r = Rect("List Fade", parent, new Vector2(width, 32), pos, null);
            var image = r.gameObject.AddComponent<Image>(); image.sprite = sprite; image.type = Image.Type.Sliced; image.color = Panel; image.raycastTarget = false; image.gameObject.SetActive(false); image.transform.SetAsLastSibling();
            return image;
        }

        static void UpdateNewUiFades()
        {
            float max = newUiContent == null || newUiViewport == null ? 0 : Mathf.Max(0, newUiContent.rect.height - newUiViewport.rect.height);
            bool canScroll = max > 0.01f;
            if (newUiTopFade != null) newUiTopFade.gameObject.SetActive(canScroll && newUiScroll > 0.5f);
            if (newUiBottomFade != null) newUiBottomFade.gameObject.SetActive(canScroll && newUiScroll < max - 0.5f);
        }

        static void UiVersionToggle(Transform parent, float width, float height, float controlScale = 1f)
        {
            var label = newUi ? "OLD UI" : "NEW UI";
            float s = Mathf.Max(0.1f, controlScale);
            NewUiButton(parent, label, new Vector2(-width / 2 + 68 * s, -height / 2 + 32 * s), new Vector2(112 * s, 38 * s), true, () =>
            {
                newUi = !newUi;
                Draw(true);
            }, Mathf.Max(8, Mathf.RoundToInt(19 * s)));
        }

        // What the LunarLink.Update module knows (its GET /update answer, through the core's Updates slot); on = false
        // while that module is off.
        sealed class UpdateInfo
        {
            public bool on, busy, auto; public string running, state, latest, detail, error;
            // how many things have an update: the release, modules, plugins (the UPDATES tab's marker)
            public int Waiting => (state == "available" ? 1 : 0) + modules.Count(m => m.state == "available") + plugins.Count(p => p.state == "available");
            public List<(string name, string state, string note, string built, string error, string version, string installed)> modules = new List<(string, string, string, string, string, string, string)>();
            public List<(string name, string kind, string version, string latest, string state, string from, string notes, string error)> plugins = new List<(string, string, string, string, string, string, string, string)>();
        }
        static UpdateInfo ReadUpdates()
        {
            var u = new UpdateInfo(); var status = Updates.Status;
            if (status == null) return u;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(status()); var r = doc.RootElement;
                string S(System.Text.Json.JsonElement e, string k) => e.TryGetProperty(k, out var x) && x.ValueKind == System.Text.Json.JsonValueKind.String ? x.GetString() : null;
                u.on = true; u.auto = r.TryGetProperty("autoCheck", out var ac) && ac.ValueKind == System.Text.Json.JsonValueKind.True; u.running = S(r, "running"); u.state = S(r, "state"); u.latest = S(r, "latest"); u.detail = S(r, "detail"); u.error = S(r, "error");
                foreach (var m in r.GetProperty("channel").GetProperty("modules").EnumerateArray())
                    u.modules.Add((S(m, "name"), S(m, "state"), S(m, "note"), S(m, "built"), S(m, "error"), S(m, "version"), S(m, "installed")));
                if (r.TryGetProperty("plugins", out var ps))
                    foreach (var p in ps.EnumerateArray())
                        u.plugins.Add((S(p, "name"), S(p, "kind"), S(p, "version"), S(p, "latest"), S(p, "state"), S(p, "from"), S(p, "notes"), S(p, "error")));
                bool Moving(string st) => st == "checking" || st == "downloading" || st == "reloading";
                u.busy = Moving(u.state) || u.modules.Any(m => Moving(m.state)) || u.plugins.Any(p => Moving(p.state));
            }
            catch (Exception e) { u.on = true; u.error = "couldn't read the updater: " + e.Message; }
            return u;
        }

        // "Save & reload" / "Save & main menu" on a reload waiting for the main menu: the game's own save (the pause
        // menu's), and once it has finished, the game's own way back to the menu (MoonSession.ReturnToMenu); the core
        // reloads the plugin there. With "Save & reload" (solo), once that reload has started cleanly the same save is
        // loaded again the way a click on it does (the main menu's Play: the active slot, which leaving kept).
        static MoonCleaners.MoonPauseMenu saving; static float savingSince;
        static string reloadThenLoad; static float atMenuSince = -1f;
        static string SaveThenMenu(string thenLoad)
        {
            reloadThenLoad = thenLoad; atMenuSince = -1f;
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<MoonCleaners.MoonPauseMenu>());
            if (found.Length == 0) return "Couldn't find the game's pause menu: save and go to the main menu yourself";
            saving = found[0].Cast<MoonCleaners.MoonPauseMenu>(); savingSince = Time.realtimeSinceStartup;
            saving.SaveGame();
            return "Saving, then going to the main menu...";
        }
        static void SavedThenMenu()
        {
            if (saving == null) return;
            float waited = Time.realtimeSinceStartup - savingSince;
            bool busy; try { busy = saving.IsSaving; } catch { busy = false; }
            if ((busy || waited < 0.5f) && waited < 30f) return;              // the save starts within a frame or two
            saving = null;
            if (layer != null) Close();
            MoonCleaners.MoonSession.ReturnToMenu();
        }

        // at the main menu after "Save & reload": once the plugin's new copy runs, its save again
        static void LoadAgain()
        {
            if (reloadThenLoad == null || saving != null) return;
            bool atMenu = false; try { atMenu = MoonCleaners.MoonSession.InMenu && !Hot.InWorld; } catch { }
            if (!atMenu) { atMenuSince = -1f; return; }
            if (atMenuSince < 0f) { atMenuSince = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - atMenuSince < 1.5f) return;          // the menu settles
            var p = Hot.Find(reloadThenLoad); var name = reloadThenLoad;
            // the core reloads it once the menu's own loading screen is gone, which can take a few seconds: wait for that
            // (up to 30 s), and give up only on a copy that actually failed
            bool waiting = p != null && p.Error == null && (p.ReloadAtMenu || !p.Running);
            if (waiting && Time.realtimeSinceStartup - atMenuSince < 30f) return;
            reloadThenLoad = null;
            if (p == null || p.ReloadAtMenu || !p.Running || p.Error != null)
            {
                Plugin.L.LogWarning("hot window: " + name + " didn't start cleanly at the main menu, so its save wasn't loaded again" + (p?.Error != null ? ": " + p.Error : ""));
                return;
            }
            // the main menu's own Play, as a click on the save does: plugins hook it (Mars Sweepers sets up its planet
            // session there) - the start step under it (StartShift) would skip them
            var menus = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<MoonCleaners.MoonMainMenu>());
            if (menus.Length == 0) { Plugin.L.LogWarning("hot window: no main menu found, so " + name + "'s save wasn't loaded again: pick it yourself"); return; }
            Plugin.L.LogInfo("hot window: " + name + " reloaded at the main menu; loading the save again");
            menus[0].Cast<MoonCleaners.MoonMainMenu>().Play();
        }

        // a version as shown: "v1.1.0.2", or "dev build"
        static string V(string v) => v == "dev" ? "dev build" : "v" + v;
        // a hot plugin's version from its file's assembly version ("dev" for a module built here), kept until the file changes
        static readonly Dictionary<string, (DateTime at, string v)> versions = new Dictionary<string, (DateTime, string)>();
        static string FileVersion(string name)
        {
            if (!name.StartsWith("LunarLink.", StringComparison.Ordinal)) return PluginVersion(name);
            try
            {
                var f = Hot.FileOf(name); if (!System.IO.File.Exists(f)) return null;
                var at = System.IO.File.GetLastWriteTimeUtc(f);
                if (versions.TryGetValue(name, out var seen) && seen.at == at) return seen.v;
                var v = System.Reflection.AssemblyName.GetAssemblyName(f).Version;
                var s = v == null || v == new Version(1, 0, 0, 0) ? null : v == new Version(0, 0, 0, 0) ? "dev" : v.Revision > 0 ? v.ToString(4) : v.ToString(3);   // 1.0.0.0: .NET's default, no version
                versions[name] = (at, s); return s;
            }
            catch { return null; }
        }

        // another hot plugin's version: the [BepInPlugin] in the copy that's loaded (where plugins keep theirs, as Crew
        // Tools does), else its assembly version unless that's .NET's default; looked up again only when it reloads
        static readonly Dictionary<string, (System.Reflection.Assembly asm, string v)> pluginVersions = new Dictionary<string, (System.Reflection.Assembly, string)>();
        static string PluginVersion(string name)
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().LastOrDefault(a => !a.IsDynamic && string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                if (asm == null) return null;
                if (pluginVersions.TryGetValue(name, out var seen) && seen.asm == asm) return seen.v;
                string v = null;
                Type[] types; try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types)
                    if (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<BepInEx.BepInPlugin>(t) is BepInEx.BepInPlugin bp) { v = bp.Version.ToString(); break; }
                if (v == null) { var av = asm.GetName().Version; if (av != null && av != new Version(1, 0, 0, 0) && av != new Version(0, 0, 0, 0)) v = av.Revision > 0 ? av.ToString(4) : av.ToString(3); }
                pluginVersions[name] = (asm, v); return v;
            }
            catch { return null; }
        }

        // The UPDATES tab: LunarLink's release first (its version, where an update stands, the button for the next step),
        // then each module in the module channel. Plugins' own updates will sit below them.
        static List<View> UpdateRows()
        {
            var rows = new List<View>();
            var u = ReadUpdates();
            if (!u.on)
            {
                rows.Add(new View { Name = "LunarLink", State = "updates off", StateColour = Dim,
                    Detail = "The LunarLink.Update module isn't running: turn it on in HOT PLUGINS (tick Show LunarLink modules)" });
                return rows;
            }
            var v = new View { Name = "Lunar Link " + u.running, Warm = true };
            switch (u.state)
            {
                case "available": v.State = u.latest + " available"; v.StateColour = Accent; break;
                case "restart": v.State = "restart to finish"; v.StateColour = Accent; break;
                case "failed": v.State = "failed"; v.StateColour = Red; break;
                case "up to date": case "done": v.State = "up to date"; v.StateColour = Green; break;
                case "idle": v.State = "not checked"; v.StateColour = Dim; break;
                default: v.State = u.state + "..."; v.StateColour = Ink; break;
            }
            v.Detail = u.error != null ? "<color=#E6524A>" + Short(u.error) + "</color>" : u.detail ?? "Press Check for updates to ask GitHub";
            var latest = u.latest;
            bool restart = u.state == "restart";
            if (u.state == "available")
                v.Buttons.Add(("Update", () => Act(() => { Updates.Apply?.Invoke(""); return "Updating LunarLink to " + latest + "..."; }), true));
            rows.Add(v);
            foreach (var m in u.modules)
            {
                var r = new View { Name = DisplayNames.For(m.name), Warm = true };
                switch (m.state)
                {
                    case "available": r.State = m.version != null ? m.version + " available" : "newer build"; r.StateColour = Accent; break;
                    case "needs core": r.State = "needs a newer core"; r.StateColour = Dim; break;
                    case "updated": r.State = "updated"; r.StateColour = Green; break;
                    case "up to date": r.State = "up to date"; r.StateColour = Green; break;
                    case "failed": r.State = "failed"; r.StateColour = Red; break;
                    default: r.State = m.state + "..."; r.StateColour = Ink; break;
                }
                r.Detail = m.error != null ? "<color=#E6524A>" + Short(m.error) + "</color>"
                         : m.state == "needs core" ? "Built for a newer LunarLink core: it comes with the next LunarLink release (one restart)"
                         : (m.installed != null ? (m.state == "available" ? "you have " : "") + V(m.installed) + "   ·   " : "") + (m.note != null ? Short(m.note) : "") + (m.built != null ? "   ·   built " + m.built : "");
                var name = m.name;
                if (m.state == "available" && !restart) r.Buttons.Add(("Update", () => Act(() => { Updates.Apply?.Invoke(name); return "Updating " + name + "..."; }), true));
                rows.Add(r);
            }
            // plugins that update themselves (they answer UpdateFeed()): from where they say
            foreach (var p in u.plugins)
            {
                var r = new View { Name = DisplayNames.For(p.name) + " " + p.version, Warm = true };
                switch (p.state)
                {
                    case "available": r.State = p.latest + " available"; r.StateColour = Accent; break;
                    case "restart": r.State = "restart to finish"; r.StateColour = Accent; break;
                    case "updated": r.State = "updated"; r.StateColour = Green; break;
                    case "up to date": r.State = "up to date"; r.StateColour = Green; break;
                    case "failed": r.State = "failed"; r.StateColour = Red; break;
                    default: r.State = p.state + "..."; r.StateColour = Ink; break;
                }
                r.Detail = p.error != null ? "<color=#E6524A>" + Short(p.error) + "</color>"
                         : "from " + p.from + (p.kind == "hot" ? " (hot: reloads in place)" : " (needs a restart)") + (p.notes != null ? "   ·   " + Short(p.notes.Trim().Split('\n')[0].TrimStart('-', ' ', '*')) : "");
                var name = p.name;
                if (p.state == "available" && !restart) r.Buttons.Add(("Update", () => Act(() => { Updates.Apply?.Invoke("plugin:" + name); return "Updating " + name + "..."; }), true));
                rows.Add(r);
            }
            return rows;
        }

        // a tab: a rounded pill, orange with dark bold text when open, grey with light text when not (as Quantum
        // Conveyors' tabs)
        static void Tab(Transform parent, string label, bool on, Vector2 pos, Action pick, float width, string help)
        {
            var r = Rect("Tab", parent, new Vector2(width, 46), pos, on ? Accent : Btn); Round(r.GetComponent<Image>(), button);
            var b = r.gameObject.AddComponent<UnityEngine.UI.Button>(); b.targetGraphic = r.GetComponent<Image>();
            actions[r.gameObject.GetInstanceID()] = pick;
            float labelWidth = width - 54f;
            float groupWidth = labelWidth + 8f + 24f;
            float groupLeft = -groupWidth / 2f;
            Label(r, "<b>" + label + "</b>", new Vector2(groupLeft + labelWidth / 2f, 0), new Vector2(labelWidth, 40), 20, on ? Ink : Text, TextAnchor.MiddleCenter);
            TabHelp(parent, r, new Vector2(groupLeft + labelWidth + 8f + 12f, 0), help, Btn, Text, new Vector2(pos.x + width / 2f, pos.y - 150f));
        }

        static void TabHelp(Transform tipParent, Transform tab, Vector2 pos, string text, Color dotColor, Color textColor, Vector2 tipPos)
        {
            EnsureSprites();
            var dot = Rect("TabHelp", tab, new Vector2(24, 24), pos, dotColor);
            var img = dot.GetComponent<Image>(); img.sprite = circle; img.type = Image.Type.Sliced;
            var q = Label(dot, "?", Vector2.zero, new Vector2(22, 22), 15, textColor, TextAnchor.MiddleCenter); q.fontStyle = FontStyle.Bold;
            var tip = Rect("Tip", tipParent, new Vector2(500, 150), tipPos, TipBg);
            var tx = Label(tip, text, Vector2.zero, new Vector2(472, 126), 17, Text, TextAnchor.UpperLeft);
            tx.verticalOverflow = VerticalWrapMode.Overflow;
            tip.gameObject.SetActive(false);
            tips.Add((dot, tip));
        }

        // A track along the list's right edge with a thumb for the rows in view; a click above or below the thumb moves
        // a page that way.
        static void ScrollBar(float listTop, float height, int total, int shown)
        {
            var track = Rect("ScrollTrack", panel, new Vector2(14, height), new Vector2(W / 2 - 22, listTop - height / 2), Track);
            float thumbH = Math.Max(30f, height * shown / total);
            float thumbY = height / 2 - thumbH / 2 - (height - thumbH) * first / Math.Max(1, total - shown);
            var thumb = Rect("ScrollThumb", track, new Vector2(10, thumbH), new Vector2(0, thumbY), Dim);
            thumb.GetComponent<Image>().raycastTarget = false;
            var b = track.gameObject.AddComponent<UnityEngine.UI.Button>(); b.targetGraphic = track.GetComponent<Image>();
            actions[track.gameObject.GetInstanceID()] = () =>
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(track, Mouse.current.position.ReadValue(), null, out var local);
                Scroll(local.y > thumbY ? -shown : shown);
            };
        }

        static void Checkbox(Transform parent, string label, bool on, Vector2 pos, Action flip)
        {
            var r = Rect("Checkbox", parent, new Vector2(380, 34), pos, new Color(0, 0, 0, 0));
            var b = r.gameObject.AddComponent<UnityEngine.UI.Button>(); b.targetGraphic = r.GetComponent<Image>();
            actions[r.gameObject.GetInstanceID()] = flip;
            var box = Rect("Box", r, new Vector2(26, 26), new Vector2(-178, 0), Btn); box.GetComponent<Image>().raycastTarget = false; Round(box.GetComponent<Image>(), button);
            if (on) { var tick = Rect("Tick", box, new Vector2(14, 14), Vector2.zero, Accent); tick.GetComponent<Image>().raycastTarget = false; }
            Label(r, label, new Vector2(18, 0), new Vector2(340, 30), 18, Ink, TextAnchor.MiddleLeft);
        }

        static void ToggleButton(Transform parent, string label, bool on, Vector2 pos, Action flip)
        {
            Label(parent, label, new Vector2(pos.x - 135, pos.y), new Vector2(260, 34), 18, Ink, TextAnchor.MiddleRight);
            Button(parent, on ? "On" : "Off", new Vector2(pos.x + 55, pos.y), new Vector2(110, 40), flip, on);
        }

        static void DrawRow(View v, float y)
        {
            var row = Rect("Row", panel, new Vector2(W - 70, RowH), new Vector2(-15, y), Panel);
            Shade(row, v.Warm, v.Disabled);
            if (v.Buttons.Count == 0)            // the API tab: a long path, its group on the right, the help under it
            {
                Label(row, "<b>" + v.Name + "</b>", new Vector2(-W / 2 + 382, 14), new Vector2(660, 34), 21, Ink, TextAnchor.MiddleLeft);
                Label(row, v.State, new Vector2(W / 2 - 165, 14), new Vector2(200, 34), 18, v.StateColour, TextAnchor.MiddleRight);
                Label(row, v.Detail, new Vector2(-15, -18), new Vector2(W - 150, 28), 16, Dim, TextAnchor.MiddleLeft);
                return;
            }
            Label(row, "<b>" + v.Name + "</b>", new Vector2(-W / 2 + 222, 14), new Vector2(330, 34), 23, Ink, TextAnchor.MiddleLeft);
            if (!string.IsNullOrEmpty(v.State)) Label(row, v.State, new Vector2(-W / 2 + 492, 14), new Vector2(200, 34), 20, v.StateColour, TextAnchor.MiddleLeft);
            Label(row, v.Detail, new Vector2(-W / 2 + 357, -18), new Vector2(600, 28), 17, Dim, TextAnchor.MiddleLeft);
            float x = W / 2 - 131;
            for (int i = v.Buttons.Count - 1; i >= 0; i--)
            {
                var (label, act, accent) = v.Buttons[i];
                var shownLabel = v.UpdateAvailable && label == "Reload" ? "Update" : label;
                var buttonAccent = accent || (v.UpdateAvailable && label == "Reload");
                var button = Button(row, shownLabel, new Vector2(x, 0), new Vector2(130, 46), act, buttonAccent);
                if (v.Disabled && label == "Reload") button.gameObject.AddComponent<CanvasGroup>().alpha = 0.8f;
                x -= 145;
            }
            if (v.Disabled)
            {
                // Fade the disabled row's background/text, but leave its Enable
                // button fully visible and orange so the available action is clear.
                foreach (Transform child in row)
                {
                    if (child.GetComponent<Button>() != null || child.GetComponentInParent<Button>() != null) continue;
                    var group = child.gameObject.AddComponent<CanvasGroup>(); group.alpha = 0.8f;
                }
            }
            if (v.Tip != null) Help(panel, new Vector2(-15 + x + 30, y), v.Tip, new Vector2(720, 240), new Vector2(-15, y - RowH / 2 - 130));
        }

        static string Done(Hot.Plugin p, string ok) => p == null ? "Not found" : p.Error != null ? "Failed: " + Short(p.Error) : ok;

        static string Short(string s) { s = s.Split('\n')[0]; return s.Length > 110 ? s.Substring(0, 110) + "…" : s; }

        static void Act(Func<string> what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { message = what() + " (" + clock.ElapsedMilliseconds + " ms)"; }
            catch (Exception e) { message = "Error: " + e.Message; Plugin.L.LogWarning("hot window: " + e); }
            if (layer != null) Draw(true);          // a reload of this module from its own row has closed this copy
        }

        // A left-click press: runs the button under the mouse.
        static void Click()
        {
            var mouse = Mouse.current; var es = EventSystem.current;
            if (ray == null || mouse == null || es == null || !mouse.leftButton.wasPressedThisFrame) return;
            var ev = new PointerEventData(es) { position = mouse.position.ReadValue() };
            var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>(); ray.Raycast(ev, hits);
            var en = hits.GetEnumerator();
            while (en.MoveNext())
            {
                var b = en.Current.gameObject?.GetComponentInParent<UnityEngine.UI.Button>();
                if (b != null && actions.TryGetValue(b.gameObject.GetInstanceID(), out var act)) { act(); return; }
            }
        }

        static RectTransform Rect(string name, Transform parent, Vector2 size, Vector2 pos, Color? bg)
        {
            var go = new GameObject(name); var r = go.AddComponent<RectTransform>(); r.SetParent(parent, false);
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f); r.sizeDelta = size; r.anchoredPosition = pos;
            if (bg.HasValue) { var img = go.AddComponent<Image>(); img.color = bg.Value; Round(img, rounded); }
            return r;
        }

        // ------------------------------------------------ the rounded look (drawn once at 2x, so corners stay smooth)
        static Sprite rounded, button, circle, warm, cool, grey, pill;

        static void EnsureSprites()
        {
            // Hot reloads can tear down one generated sprite before the others.
            // Recreate each missing asset independently instead of assuming the
            // rounded sprite means the complete set is still alive.
            if (rounded == null) rounded = RoundedSprite(24, "LunarLink Rounded");
            if (button == null) button = RoundedSprite(12, "LunarLink Button");
            if (circle == null) circle = RoundedSprite(128, "LunarLink Circle");
            if (pill == null) pill = PillSprite("LunarLink Toast Pill");
            if (warm == null) warm = Gradient(new Color(Accent.r, Accent.g, Accent.b, 0.22f), new Color(Accent.r, Accent.g, Accent.b, 0f), "LunarLink Warm");
            if (cool == null) cool = Gradient(new Color(Ink.r, Ink.g, Ink.b, 0.24f), new Color(Ink.r, Ink.g, Ink.b, 0f), "LunarLink Cool");
            if (grey == null) grey = Gradient(new Color(0.48f, 0.48f, 0.48f, 0.22f), new Color(0.48f, 0.48f, 0.48f, 0f), "LunarLink Disabled");
            if (newUiFolderIcon == null) newUiFolderIcon = FolderIcon();
        }

        static Sprite FolderIcon()
        {
            const int size = 48; var color = Ink;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Quantum Folder Icon", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[size * size];
            void Put(int x, int y) { if (x >= 0 && x < size && y >= 0 && y < size) pixels[y * size + x] = color; }
            for (var y = 12; y <= 36; y++) for (var x = 6; x <= 40; x++) Put(x, y);
            for (var y = 36; y <= 40; y++) for (var x = 10; x <= 22; x++) Put(x, y);
            for (var x = 8; x <= 20; x++) Put(x, 38);
            texture.SetPixels(pixels); texture.Apply(false, true);
            return Sprite.Create(texture, new UnityEngine.Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 200f);
        }

        static Sprite RoundedSprite(int radius, string name) => Sprite256(256, radius, name, (x, y) => Color.white);

        // A true capsule at the toast's native aspect ratio.  It is rendered as
        // a simple image rather than 9-sliced, so the corner radius stays exactly
        // half the height when the whole toast is scaled.
        static Sprite PillSprite(string name)
        {
            const int w = 720, h = 34, radius = 17;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                float cx = Mathf.Clamp(fx, radius, w - radius), cy = Mathf.Clamp(fy, radius, h - radius);
                var c = Color.white;
                c.a = Mathf.Clamp01(radius + 0.5f - Vector2.Distance(new Vector2(fx, fy), new Vector2(cx, cy)));
                px[y * w + x] = c;
            }
            t.SetPixels(px); t.Apply(false, true);
            return Sprite.Create(t, new UnityEngine.Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 200f);
        }

        // a soft colour across a row, left to right, with the rows' rounded corners
        static Sprite Gradient(Color left, Color right, string name) => Sprite256(128, 24, name, (x, y) => Color.Lerp(left, right, x / 255f));

        static Sprite Sprite256(int h, int radius, string name, Func<int, int, Color> colour)
        {
            const int w = 256;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f, cx = Mathf.Clamp(fx, radius, w - radius), cy = Mathf.Clamp(fy, radius, h - radius);
                var c = colour(x, y); c.a *= Mathf.Clamp01(radius + 0.75f - Vector2.Distance(new Vector2(fx, fy), new Vector2(cx, cy)));
                px[y * w + x] = c;
            }
            t.SetPixels(px); t.Apply(false, true);
            return Sprite.Create(t, new UnityEngine.Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 200f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        }

        // a rounded surface; small ones (the scroll bar, the checkbox tick) stay square
        static void Round(Image img, Sprite sprite)
        {
            if (img == null) return;
            var size = img.rectTransform.sizeDelta;
            if (size.x < 24 || size.y < 24) return;
            EnsureSprites(); img.sprite = sprite == null ? rounded : sprite; img.type = Image.Type.Sliced;
        }

        // the soft gradient across a row: warm (orange) for something live, cool otherwise
        static void Shade(RectTransform row, bool warmTone, bool muted)
        {
            EnsureSprites();
            var g = Rect("Shade", row, row.sizeDelta, Vector2.zero, null);
            var img = g.gameObject.AddComponent<Image>(); img.sprite = muted ? grey : warmTone ? warm : cool; img.type = Image.Type.Sliced; img.raycastTarget = false;
            g.SetAsFirstSibling();
        }

        // a round "?" with a dark tooltip that shows while the mouse is over it (Tooltips, each frame)
        static readonly List<(RectTransform dot, RectTransform tip)> tips = new List<(RectTransform, RectTransform)>();
        static void Help(Transform parent, Vector2 pos, string text, Vector2 tipSize, Vector2 tipPos)
        {
            EnsureSprites();
            var dot = Rect("Help", parent, new Vector2(28, 28), pos, Btn);
            var img = dot.GetComponent<Image>(); img.sprite = circle; img.type = Image.Type.Sliced;
            var q = Label(dot, "?", Vector2.zero, new Vector2(24, 24), 16, Text, TextAnchor.MiddleCenter); q.fontStyle = FontStyle.Bold;
            var tip = Rect("Tip", parent, tipSize, tipPos, TipBg);
            var tx = Label(tip, text, Vector2.zero, tipSize - new Vector2(26, 22), 13, Text, TextAnchor.UpperLeft);
            tx.horizontalOverflow = HorizontalWrapMode.Wrap;
            tx.verticalOverflow = VerticalWrapMode.Overflow;
            tip.gameObject.SetActive(false);
            tips.Add((dot, tip));
        }

        static void Tooltips()
        {
            var mouse = Mouse.current; if (mouse == null) return;
            var at = mouse.position.ReadValue();
            for (int i = tips.Count - 1; i >= 0; i--)
            {
                var (dot, tip) = tips[i];
                if (dot == null || tip == null) { tips.RemoveAt(i); continue; }
                bool over = dot.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(dot, at, null);
                if (tip.gameObject.activeSelf != over) { tip.gameObject.SetActive(over); if (over) tip.SetAsLastSibling(); }
            }
        }

        // Lock the player exactly like Quantum Conveyors does while its menu is open.
        // Cursor visibility alone is not enough: the game's movement actions would
        // otherwise continue receiving WASD, jump, tool, and interaction input.
        static void Freeze()
        {
            lockBefore = Cursor.lockState; visibleBefore = Cursor.visible;
            var found = FindWalker();
            if (found != null)
            {
                previousWalkerBlocked = found.UIBlocked;
                previousWalkerEnabled = found.enabled;
                playerInput = found.inputTemplate;
                if (playerInput != null)
                {
                    previousPlayerInputEnabled = playerInput.enabled;
                    playerInput.Disable();
                }
                else DisableFallbackInputAssets();
                found.UIBlocked = true;
            }
            else DisableFallbackInputAssets();
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        static void Unfreeze()
        {
            try
            {
                var found = FindWalker();
                if (found != null) { found.UIBlocked = previousWalkerBlocked; found.enabled = previousWalkerEnabled; }
            }
            catch (Exception e) { Plugin.L.LogWarning("window input restore skipped: " + e.Message); }
            try { if (playerInput != null && previousPlayerInputEnabled) playerInput.Enable(); }
            catch (Exception e) { Plugin.L.LogWarning("window player input restore skipped: " + e.Message); }
            playerInput = null;
            foreach (var asset in fallbackInputAssets)
                try { if (asset != null) asset.Enable(); } catch { }
            fallbackInputAssets.Clear();
            Cursor.lockState = lockBefore; Cursor.visible = visibleBefore;
        }

        static MoonWalker FindWalker()
        {
            if (walker != null) return walker;
            var cam = Camera.main ?? Camera.current;
            if (cam != null)
            {
                var found = cam.GetComponent(Il2CppType.Of<MoonWalker>());
                if (found == null) found = cam.GetComponentInParent(Il2CppType.Of<MoonWalker>());
                if (found == null) found = cam.transform.root.GetComponentInChildren(Il2CppType.Of<MoonWalker>());
                walker = found as MoonWalker;
                if (walker != null) return walker;
            }
            try
            {
                foreach (var candidate in UnityEngine.Object.FindObjectsOfType<MoonWalker>(true))
                    if (candidate != null) { walker = candidate; return walker; }
            }
            catch { }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i); if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var found = root.GetComponentInChildren(Il2CppType.Of<MoonWalker>()) as MoonWalker;
                    if (found != null) { walker = found; return walker; }
                }
            }
            foreach (var item in Resources.FindObjectsOfTypeAll(Il2CppType.Of<MoonWalker>()))
            {
                walker = item as MoonWalker;
                if (walker != null && walker.gameObject.scene.IsValid()) return walker;
            }
            return walker;
        }

        static void DisableFallbackInputAssets()
        {
            foreach (var item in Resources.FindObjectsOfTypeAll(Il2CppType.Of<InputActionAsset>()))
            {
                var asset = item as InputActionAsset;
                if (asset == null || !asset.enabled || IsUiInputAsset(asset)) continue;
                asset.Disable(); fallbackInputAssets.Add(asset);
            }
        }

        static bool IsUiInputAsset(InputActionAsset asset)
        {
            var n = asset.name ?? string.Empty;
            return n.IndexOf("ui", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static UnityEngine.UI.Text Label(Transform parent, string text, Vector2 pos, Vector2 size, int fontSize, Color color, TextAnchor align)
        {
            var t = Rect("Text", parent, size, pos, null).gameObject.AddComponent<UnityEngine.UI.Text>();
            t.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); t.text = text; t.fontSize = fontSize; t.color = color; t.alignment = align;
            t.supportRichText = true; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        static RectTransform Button(Transform parent, string label, Vector2 pos, Vector2 size, Action action, bool accent = false)
        {
            bool close = label == "×";
            var r = Rect("Button", parent, size, pos, close ? new Color(0, 0, 0, 0) : accent ? Accent : Btn); Round(r.GetComponent<Image>(), button);
            var b = r.gameObject.AddComponent<UnityEngine.UI.Button>(); b.targetGraphic = r.GetComponent<Image>();
            actions[r.gameObject.GetInstanceID()] = action;
            var t = Label(r, label, Vector2.zero, size - new Vector2(8, 4), close ? 34 : 19, accent ? Ink : close ? Faint : Text, TextAnchor.MiddleCenter);
            if (accent) t.fontStyle = FontStyle.Bold;
            return r;
        }
    }
}
