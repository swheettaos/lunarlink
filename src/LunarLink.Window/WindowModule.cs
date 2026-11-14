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
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
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
        static bool bepTab => tab == 1;
        static string apiGroup;                // API tab: the area or plugin shown (null = all)

        // one row as drawn, whichever tab it comes from
        sealed class View
        {
            public string Name, State, Detail; public Color StateColour;
            public List<(string label, Action act, bool accent)> Buttons = new List<(string, Action, bool)>();
        }

        static GameObject layer; static RectTransform panel; static GraphicRaycaster ray;
        static readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        static Key key = Key.None; static string keyName;
        static float nextRefresh; static string drawn, message;
        static CursorLockMode lockBefore; static bool visibleBefore;

        internal static void Update()
        {
            if (reopenTab >= 0 && layer == null) { tab = reopenTab; reopenTab = -1; Open(); message = "The window reloaded with the new build"; Draw(true); return; }
            var kb = Keyboard.current; if (kb == null) return;
            bool typing = false; try { typing = MoonCleaners.MoonInput.Typing; } catch { }   // not while typing in chat
            if (!typing && Pressed(kb)) { if (layer == null) Open(); else Close(); return; }
            if (layer == null) return;
            Click(); Tooltips();
            var wheel = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
            if (wheel != 0f) { Scroll(wheel > 0 ? -1 : 1); return; }
            if (Time.realtimeSinceStartup >= nextRefresh) Draw(false);
        }

        internal static void LateUpdate() { if (layer != null) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } }

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
            if (EventSystem.current == null) { ownEventSystem = new GameObject("LunarLink EventSystem"); ownEventSystem.AddComponent<EventSystem>(); UnityEngine.Object.DontDestroyOnLoad(ownEventSystem); }
            message = null; first = 0;
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
            foreach (var s in new[] { rounded, button, circle, warm, cool })
                if (s != null) { UnityEngine.Object.Destroy(s.texture); UnityEngine.Object.Destroy(s); }
            rounded = button = circle = warm = cool = null;
        }

        static void Close()
        {
            actions.Clear(); tips.Clear();
            if (layer != null) UnityEngine.Object.Destroy(layer);
            layer = null; panel = null; ray = null; drawn = null;
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

        static Color StateColour(string state) => state == "running" ? Green : state == "failed" ? Red : state.Contains("next start") ? Accent : state == "disabled" || state == "off" ? Dim : Ink;

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
                    var v = new View { Name = e.Name + (e.Version != null ? "  " + e.Version : ""), State = e.State, StateColour = StateColour(e.State),
                                       Detail = e.File + (e.NeededBy.Count > 0 ? "   needed by " + string.Join(", ", e.NeededBy) : "") + (e.State.Contains("next start") ? "   <color=#FF7A2E>restart the game to apply</color>" : "") };
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
                var v = new View { Name = r.Name, State = r.State, StateColour = StateColour(r.State),
                    Detail = r.Error != null ? "<color=#E6524A>" + Short(r.Error) + "</color>"
                        : (FileVersion(r.Name) is string fv ? V(fv) + "   ·   " : "") + (r.Built != null ? "built " + r.Built : "") + (r.Newer ? "   <color=#FF7A2E>newer build on disk</color>" : "") + (r.NeedsRestart ? "   restart to register buildings" : "") };
                bool off = r.State == "disabled"; var name = r.Name;
                v.Buttons.Add((off ? "Enable" : "Disable", () => Act(() => off ? Done(Hot.Enable(name, reload: false), "Enabled " + name) : Done(Hot.Disable(name), "Disabled " + name)), off));
                v.Buttons.Add(("Reload", () => Act(() => Done(Hot.Enable(name, reload: true), "Reloaded " + name)), false));
                list.Add(v);
            }
            return list;
        }

        // Rebuilds the window when what it shows has changed (checked once a second, and after every button).
        static void Draw(bool force)
        {
            nextRefresh = Time.realtimeSinceStartup + 1f;
            var rows = Views(out int hidden, out int modules);
            first = Math.Max(0, Math.Min(first, rows.Count - MaxRows));
            var sig = tab + "|" + apiGroup + "|" + string.Join("|", rows.Select(r => r.Name + r.State + r.Detail)) + "|" + message + "|" + first + "|" + hidden + "|" + ReadUpdates().busy + "|" + ReadUpdates().Waiting;
            if (!force && sig == drawn) return;
            drawn = sig;
            actions.Clear(); tips.Clear();
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            // one size on every tab and filter: room for MaxRows rows and the message line, the scroll bar for more
            int shown = Math.Min(MaxRows, rows.Count);
            float listH = MaxRows * (RowH + Gap);
            float h = Head + 210f + listH + 44f;
            panel = Rect("Window", layer.transform, new Vector2(W, h), Vector2.zero, Panel);
            // the header: the title in the middle, a "?" beside it, the close cross on the right
            Label(panel, "LunarLink", new Vector2(0, h / 2 - 38), new Vector2(400, 56), 32, Ink, TextAnchor.MiddleCenter);
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
            float tx = -(230 + 260 + 130 + 180 + 3 * 10) / 2f;
            Tab(panel, "HOT PLUGINS", tab == 0, new Vector2(tx + 115, top - 26), () => { tab = 0; first = 0; message = null; Draw(true); }, 230); tx += 240;
            Tab(panel, "BEPINEX PLUGINS", tab == 1, new Vector2(tx + 130, top - 26), () => { tab = 1; first = 0; message = null; Draw(true); }, 260); tx += 270;
            Tab(panel, "API", tab == 2, new Vector2(tx + 65, top - 26), () => { tab = 2; first = 0; message = null; Draw(true); }, 130); tx += 140;
            Tab(panel, "UPDATES" + (waiting > 0 ? "  <color=" + (tab == 3 ? "#1C2232" : "#FF7A2E") + ">●" + waiting + "</color>" : ""), tab == 3, new Vector2(tx + 90, top - 26), () => { tab = 3; first = 0; message = null; Draw(true); }, 180);
            Label(panel, (tab == 3 ? "LunarLink and plugins with updates" : tab == 2 ? "every endpoint (GET /help)" : bepTab ? "BepInEx/plugins/" : "BepInEx/hot/") + "   ·   " + keyLabel + " closes", new Vector2(-W / 2 + 245, top - 88), new Vector2(420, 26), 17, Dim, TextAnchor.MiddleLeft);
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
                if (u.on && !u.busy)
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
                Button(panel, "Reload changed", new Vector2(W / 2 - 295, top - 88), new Vector2(200, 42), () => Act(() =>
                {
                    var done = Hot.ReloadChanged();
                    return done.Count == 0 ? "No newer builds on disk" : "Reloaded " + string.Join(", ", done.Select(p => p.Name));
                }), true);
                Button(panel, "Rescan", new Vector2(W / 2 - 115, top - 88), new Vector2(130, 42), () => Act(() =>
                {
                    var (added, removed) = Hot.RescanAll();
                    return added.Count + removed.Count == 0 ? "No new or removed DLLs" : "Added " + added.Count + ", removed " + removed.Count;
                }));
                Checkbox(panel, "Show LunarLink modules (" + modules + ")", ShowModules?.Value ?? false, new Vector2(-W / 2 + 226, top - 134), () =>
                {
                    if (ShowModules != null) ShowModules.Value = !ShowModules.Value;
                    first = 0; Draw(true);
                });
            }
            else
            {
                int pending = rows.Count(r => r.State.Contains("next start"));
                Label(panel, pending > 0 ? "<color=#FF7A2E><b>RESTART REQUIRED:</b> " + pending + " change" + (pending == 1 ? "" : "s") + " take effect at the next game start</color>"
                                         : "Plugins load at game start: Disable and Enable apply at the next start (restart required).",
                    new Vector2(-W / 2 + 380, top - 134), new Vector2(720, 28), 18, Dim, TextAnchor.MiddleLeft);
            }
            float listTop = top - 165f;
            if (rows.Count == 0)
                Label(panel, tab == 2 ? "No /help: the LunarLink.Api module isn't running" : hidden > 0 ? "Only LunarLink's modules are in BepInEx/hot/ (tick the box to see them)" : bepTab ? "No plugins" : "No DLLs in BepInEx/hot/",
                    new Vector2(0, listTop - RowH / 2), new Vector2(W - 60, RowH), 22, Dim, TextAnchor.MiddleCenter);
            float y = listTop;
            for (int i = first; i < first + shown; i++) { DrawRow(rows[i], y - RowH / 2); y -= RowH + Gap; }
            if (rows.Count > MaxRows) ScrollBar(listTop, listH - Gap, rows.Count, shown);
            if (message != null) Label(panel, message, new Vector2(0, -h / 2 + 30), new Vector2(W - 60, 36), 19, Ink, TextAnchor.MiddleLeft);
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
            var v = new View { Name = "LunarLink " + u.running };
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
            if (u.state == "available")
                v.Buttons.Add(("Update", () => Act(() => { Updates.Apply?.Invoke(""); return "Updating LunarLink to " + latest + "..."; }), true));
            rows.Add(v);
            foreach (var m in u.modules)
            {
                var r = new View { Name = m.name };
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
                if (m.state == "available") r.Buttons.Add(("Update", () => Act(() => { Updates.Apply?.Invoke(name); return "Updating " + name + "..."; }), true));
                rows.Add(r);
            }
            // plugins that update themselves (they answer UpdateFeed()): from where they say
            foreach (var p in u.plugins)
            {
                var r = new View { Name = p.name + " " + p.version };
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
                if (p.state == "available") r.Buttons.Add(("Update", () => Act(() => { Updates.Apply?.Invoke("plugin:" + name); return "Updating " + name + "..."; }), true));
                rows.Add(r);
            }
            return rows;
        }

        // a tab: a rounded pill, orange with dark bold text when open, grey with light text when not (as Quantum
        // Conveyors' tabs)
        static void Tab(Transform parent, string label, bool on, Vector2 pos, Action pick, float width)
        {
            var r = Rect("Tab", parent, new Vector2(width, 46), pos, on ? Accent : Btn); Round(r.GetComponent<Image>(), button);
            var b = r.gameObject.AddComponent<UnityEngine.UI.Button>(); b.targetGraphic = r.GetComponent<Image>();
            actions[r.gameObject.GetInstanceID()] = pick;
            Label(r, "<b>" + label + "</b>", Vector2.zero, new Vector2(width - 10, 40), 20, on ? Ink : Text, TextAnchor.MiddleCenter);
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

        static void DrawRow(View v, float y)
        {
            var row = Rect("Row", panel, new Vector2(W - 70, RowH), new Vector2(-15, y), Panel);
            Shade(row, v.State == "running" || v.State == "available");   // warm for something live or waiting
            if (v.Buttons.Count == 0)            // the API tab: a long path, its group on the right, the help under it
            {
                Label(row, "<b>" + v.Name + "</b>", new Vector2(-W / 2 + 382, 14), new Vector2(660, 34), 21, Ink, TextAnchor.MiddleLeft);
                Label(row, v.State, new Vector2(W / 2 - 165, 14), new Vector2(200, 34), 18, v.StateColour, TextAnchor.MiddleRight);
                Label(row, v.Detail, new Vector2(-15, -18), new Vector2(W - 150, 28), 16, Dim, TextAnchor.MiddleLeft);
                return;
            }
            Label(row, "<b>" + v.Name + "</b>", new Vector2(-W / 2 + 222, 14), new Vector2(330, 34), 23, Ink, TextAnchor.MiddleLeft);
            Label(row, v.State, new Vector2(-W / 2 + 492, 14), new Vector2(200, 34), 20, v.StateColour, TextAnchor.MiddleLeft);
            Label(row, v.Detail, new Vector2(-W / 2 + 357, -18), new Vector2(600, 28), 17, Dim, TextAnchor.MiddleLeft);
            float x = W / 2 - 131;
            for (int i = v.Buttons.Count - 1; i >= 0; i--) { var (label, act, accent) = v.Buttons[i]; Button(row, label, new Vector2(x, 0), new Vector2(130, 46), act, accent); x -= 145; }
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
        static Sprite rounded, button, circle, warm, cool;

        static void EnsureSprites()
        {
            if (rounded != null) return;
            rounded = RoundedSprite(24, "LunarLink Rounded");
            button = RoundedSprite(12, "LunarLink Button");
            circle = RoundedSprite(128, "LunarLink Circle");
            warm = Gradient(new Color(Accent.r, Accent.g, Accent.b, 0.22f), new Color(Accent.r, Accent.g, Accent.b, 0f), "LunarLink Warm");
            cool = Gradient(new Color(Ink.r, Ink.g, Ink.b, 0.24f), new Color(Ink.r, Ink.g, Ink.b, 0f), "LunarLink Cool");
        }

        static Sprite RoundedSprite(int radius, string name) => Sprite256(256, radius, name, (x, y) => Color.white);

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
        static void Shade(RectTransform row, bool warmTone)
        {
            EnsureSprites();
            var g = Rect("Shade", row, row.sizeDelta, Vector2.zero, null);
            var img = g.gameObject.AddComponent<Image>(); img.sprite = warmTone ? warm : cool; img.type = Image.Type.Sliced; img.raycastTarget = false;
            g.SetAsFirstSibling();
        }

        // a round "?" with a dark tooltip that shows while the mouse is over it (Tooltips, each frame)
        static readonly List<(RectTransform dot, RectTransform tip)> tips = new List<(RectTransform, RectTransform)>();
        static void Help(Transform parent, Vector2 pos, string text, Vector2 tipSize, Vector2 tipPos)
        {
            EnsureSprites();
            var dot = Rect("Help", parent, new Vector2(30, 30), pos, Btn);
            var img = dot.GetComponent<Image>(); img.sprite = circle; img.type = Image.Type.Sliced;
            var q = Label(dot, "?", Vector2.zero, new Vector2(26, 26), 18, Text, TextAnchor.MiddleCenter); q.fontStyle = FontStyle.Bold;
            var tip = Rect("Tip", parent, tipSize, tipPos, TipBg);
            var tx = Label(tip, text, Vector2.zero, tipSize - new Vector2(28, 24), 17, Text, TextAnchor.UpperLeft);
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

        static UnityEngine.UI.Text Label(Transform parent, string text, Vector2 pos, Vector2 size, int fontSize, Color color, TextAnchor align)
        {
            var t = Rect("Text", parent, size, pos, null).gameObject.AddComponent<UnityEngine.UI.Text>();
            t.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); t.text = text; t.fontSize = fontSize; t.color = color; t.alignment = align;
            t.supportRichText = true; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        static void Button(Transform parent, string label, Vector2 pos, Vector2 size, Action action, bool accent = false)
        {
            bool close = label == "×";
            var r = Rect("Button", parent, size, pos, close ? new Color(0, 0, 0, 0) : accent ? Accent : Btn); Round(r.GetComponent<Image>(), button);
            var b = r.gameObject.AddComponent<UnityEngine.UI.Button>(); b.targetGraphic = r.GetComponent<Image>();
            actions[r.gameObject.GetInstanceID()] = action;
            var t = Label(r, label, Vector2.zero, size - new Vector2(8, 4), close ? 34 : 19, accent ? Ink : close ? Faint : Text, TextAnchor.MiddleCenter);
            if (accent) t.fontStyle = FontStyle.Bold;
        }
    }
}
