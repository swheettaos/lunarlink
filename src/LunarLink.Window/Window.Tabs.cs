using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LunarLink
{
    // What each tab lists. Every row opens (+) to its actions; one row open at a time.
    static partial class Window
    {
        static string Plural(int n, string what) => n + " " + what + (n == 1 ? "" : "s");

        // ------------------------------------------------ Hot Mods: the DLLs in BepInEx/hot/
        static bool Shown(Hot.Row r) => (ShowModules?.Value ?? false) || !r.Name.StartsWith("LunarLink.", StringComparison.Ordinal);

        static void AddHotMods()
        {
            TitleButton(false, 0, "Reload changed", 150, true, () => Act(() =>
            {
                var done = Hot.ReloadChanged();
                return done.Count == 0 ? "No newer builds on disk" : "Reloaded " + string.Join(", ", done.Select(p => DisplayNames.For(p.Name)));
            }));
            // left, beside Support file: on the right it covered the title's help dot
            TitleButton(true, 138, "Rescan", 96, false, () => Act(() =>
            {
                var (added, removed) = Hot.RescanAll();
                return added.Count + removed.Count == 0 ? "No new or removed DLLs" : "Added " + added.Count + ", removed " + removed.Count;
            }));
            AddSupportButton();

            var all = Hot.Rows(); var rows = all.Where(Shown).ToList();
            if (rows.Count == 0) NoteRow("m:none", all.Count > 0
                ? "Only Lunar Link's own modules are in BepInEx/hot/: Show them below" : "No DLLs in BepInEx/hot/");
            foreach (var r in rows) AddHotPlugin(r);

            var modules = Box(0, "Lunar Link Modules");
            bool showing = ShowModules?.Value ?? false;
            Button(modules, showing ? "Hide" : "Show", new Vector2(BoxRight - 55, 0), new Vector2(110, 34), showing,
                () => { if (ShowModules != null) ShowModules.Value = !showing; SaveConfig(); Draw(true); });
            KeyField(Box(1, "Lunar Link Keybind"));
        }

        static void AddHotPlugin(Hot.Row r)
        {
            string name = r.Name, display = DisplayNames.For(name), version = Version(name);
            bool off = r.State == "disabled" || r.State == "off at restart", waits = r.State == "reloads at main menu";
            string line2 = r.Error != null ? "<color=" + RedHex + ">" + Short(r.Error) + "</color>"
                : r.State == "off at restart" ? "<color=" + AccentHex + ">off from the next start</color>"
                : waits ? "<color=" + AccentHex + ">its new copy loads at the main menu (or the next start)</color>"
                : r.Newer ? "<color=" + AccentHex + ">newer build on disk</color>"
                : r.NeedsRestart ? "restart to register its buildings"
                : r.Wrapped ? "wrapped (experimental)" : null;
            var titleColour = r.State == "failed" ? Red : off ? new Color(Ink.r, Ink.g, Ink.b, 0.8f) : Ink;
            var actionsRow = OpeningRow("m:" + name, display
                + (version != null ? "  " + version : ""), titleColour, line2, Dim, off ? Look.Off : Look.Live, out _);
            if (actionsRow == null) return;

            RowText(actionsRow, r.Wrapped
                ? "A normal BepInEx plugin run by the wrapper: "
                + (r.Warnings.Length == 0 ? "nothing found it can't undo" : string.Join("; ", r.Warnings))
                : r.Built != null ? "built " + r.Built : "not loaded", 0, 330, 13, Dim);
            Action settings = PluginSettings.For(name, null, name + ".dll").Count > 0 || Hot.Buttons(name).Count > 0
                ? () => OpenSettings(new SettingsPage { Title = display, Name = name, File = name + ".dll", Hot = true }) : (Action)null;
            if (waits)
            {
                // a wrapped plugin that loads its own content, reloaded in a world: it waits for the main menu. Solo: the
                // whole round trip (save, menu, reload, the same save again); with a crew, leaving ends everyone's session,
                // so it stops at the menu (the game keeps a Steam lobby even solo: a crew is someone else in it)
                bool crew = false;
                try
                {
                    crew = MoonCleaners.MoonSteamLobby.InLobby
                        && Steamworks.SteamMatchmaking.GetNumLobbyMembers(new Steamworks.CSteamID(MoonCleaners.MoonSteamLobby.LobbyId)) > 1;
                }
                catch (System.Exception caught) { Quiet.Note(caught); }
                RowButtons(actionsRow, Right,
                    (crew ? "Save & menu" : "Save & reload", 130, true, () => Act(() => SaveThenMenu(crew ? null : name))),
                    ("Cancel", 84, false, () => Act(() => Done(Hot.CancelReload(name), "No reload of " + display))));
                return;
            }
            RowButtons(actionsRow, Right,
                ("Reload", 84, r.Newer, off ? null : (Action)(() => Act(() =>
                {
                    fadeIn.Add("m:" + name); reloadFlash.Add("m:" + name);
                    var p = Hot.Enable(name, reload: true);
                    return p != null && p.ReloadAtMenu
                        ? display + " " + Hot.WaitReason(p)
                        + ", so its new copy loads at the main menu. Save & reload does it all and brings you back to this save"
                        : Done(p, "Reloaded " + display);
                }))),
                (off ? "Enable" : "Disable", 86, off, () => Act(() =>
                {
                    fadeIn.Add("m:" + name);
                    return off ? Done(Hot.Enable(name, reload: false), "Enabled " + display) : Done(Hot.Disable(name), "Disabled " + display);
                })),
                ("Settings", 96, false, settings));
        }

        // the support file (the core's GET /support): everything a bug report needs, names blanked out
        static void AddSupportButton() => TitleButton(true, 0, "Support file", 130, false, () => Act(() =>
        {
            var (file, problems, error) = Support.Save();
            return error != null ? "Couldn't write the support file: " + error
                : "Saved BepInEx/" + System.IO.Path.GetFileName(file) + ": attach it to a GitHub issue"
                    + (problems.Count > 0 ? " (" + Plural(problems.Count, "problem") + " found)" : "");
        }));

        // a hot plugin's version: a module's from its file's assembly version ("dev" for one built here), a plugin's from
        // the [BepInPlugin] in the copy that's loaded, else its assembly version unless that's .NET's default 1.0.0.0;
        // each looked up again only when its file or loaded copy changes
        static readonly Dictionary<string, (object at, string v)> versions = new Dictionary<string, (object, string)>();
        static string Version(string name)
        {
            static string Of(System.Version v) => v == null || v == new System.Version(1, 0, 0, 0) ? null
                : v == new System.Version(0, 0, 0, 0) ? "dev" : v.ToString(v.Revision > 0 ? 4 : 3);
            try
            {
                if (name.StartsWith("LunarLink.", StringComparison.Ordinal))
                {
                    var f = Hot.FileOf(name); if (!System.IO.File.Exists(f)) return null;
                    object at = System.IO.File.GetLastWriteTimeUtc(f);
                    if (versions.TryGetValue(name, out var seen) && seen.at.Equals(at)) return seen.v;
                    var v = Of(System.Reflection.AssemblyName.GetAssemblyName(f).Version);
                    versions[name] = (at, v); return v;
                }
                var asm = AppDomain.CurrentDomain.GetAssemblies().LastOrDefault(a => !a.IsDynamic
                    && string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                if (asm == null) return null;
                if (versions.TryGetValue(name, out var had) && ReferenceEquals(had.at, asm)) return had.v;
                Type[] types; try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                var plugin = types.Select(t => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<BepInEx.BepInPlugin>(t))
                    .FirstOrDefault(a => a != null);
                var found = plugin != null ? plugin.Version.ToString() : Of(asm.GetName().Version);
                versions[name] = (asm, found); return found;
            }
            catch { return null; }
        }

        // ------------------------------------------------ Mods: the normal BepInEx plugins
        static void AddMods()
        {
            AddSupportButton();
            bool restart = PluginSettings.Pending || CoreWaiting || BepPlugins.List().Any(e => e.State.Contains("next start"));
            if (restart) TitleButton(false, 0, restartAt > 0f ? "Saving..." : "Restart & Apply", 170, true, restartAt > 0f ? null : (Action)Restart);
            var list = BepPlugins.List();
            if (list.Count == 0) NoteRow("b:none", "No plugins");
            foreach (var e in list) AddBepPlugin(e);
        }

        // a new LunarLink core installed (in the game, or by build.sh --install): it loads at the next start
        static bool CoreWaiting
        {
            get
            {
                try
                {
                    var f = System.IO.Path.Combine(Api.DataDir, "installed-version");
                    return System.IO.File.Exists(f) && System.IO.File.ReadAllText(f).Trim() is string v && v.Length > 0 && v != Pump.PluginVersion;
                }
                catch (Exception caught) { Quiet.Note(caught); return false; }
            }
        }

        static void AddBepPlugin(BepPlugins.Entry e)
        {
            bool off = e.State == "off" || e.State == "off from next start";
            string display = DisplayNames.For(e.Name), file = e.File, key = "b:" + file;
            string state = e.State == "running" || off || e.State == "on from next start" ? "" : "  " + e.State;
            string line2 = e.State.Contains("next start") ? "<color=" + AccentHex + ">" + e.State + ": restart the game to apply</color>"
                : e.NeededBy.Count > 0 ? "needed by " + string.Join(", ", e.NeededBy.Select(DisplayNames.For)) : null;
            var titleColour = e.State == "file gone" ? Red : off ? new Color(Ink.r, Ink.g, Ink.b, 0.8f) : Ink;
            var actionsRow = OpeningRow(key, display + (e.Version == null ? "" : "  " + e.Version) + state + (e.Self ? "   (runs this window)" : ""),
                titleColour, line2, Dim, off ? Look.Off : Look.Live, out _);
            if (actionsRow == null) return;

            RowText(actionsRow, file, 0, 330, 13, Dim);
            // on or off for the next start: the message says whether that needs a restart
            string Toggle()
            {
                fadeIn.Add(key);
                if (off) return BepPlugins.Enable(file) ?? (e.Loaded ? "Turned back on: " + file + ". It never stopped, so no restart is needed"
                                                                     : "Turned on: " + file + ". Restart the game to apply");
                return BepPlugins.Disable(file) ?? (e.Loaded ? "Turned off: " + file + ". Restart the game to apply (it keeps running until then)"
                                                             : "Turned back off: " + file + ". It never loaded, so no restart is needed");
            }
            Action toggle = e.Self || e.State == "file gone" ? null : () => Act(Toggle);
            Action settings = PluginSettings.For(e.Name, e.Guid, file).Count > 0
                ? () => OpenSettings(new SettingsPage { Title = display, Name = e.Name, Guid = e.Guid, File = file, Hot = false }) : (Action)null;
            var buttons = new List<(string, float, bool, Action)>();
            if (toggle != null) buttons.Add((off ? "Enable" : "Disable", 86, off, toggle));
            if (settings != null) buttons.Add(("Settings", 96, false, settings));
            RowButtons(actionsRow, Right, buttons.ToArray());
        }

        // ------------------------------------------------ API: every endpoint, a folder per path
        static string openFolder;

        // GET /help through the core's route table (the help lives in the Api module); empty when that module isn't running
        static List<(string group, string from, string method, string path, string help)> Endpoints()
        {
            var list = new List<(string, string, string, string, string)>();
            string json;
            try { json = Api.Dispatch(new Req { Method = "GET", Path = "/help", Query = new System.Collections.Specialized.NameValueCollection() }, "/help"); }
            catch { return list; }
            if (json == null) return list;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var g in doc.RootElement.GetProperty("groups").EnumerateArray())
                foreach (var e in g.GetProperty("endpoints").EnumerateArray())
                    list.Add((g.GetProperty("group").GetString(), g.GetProperty("from").GetString(), e.GetProperty("method").GetString(),
                              e.GetProperty("path").GetString(), e.GetProperty("help").GetString()));
            return list;
        }

        static void AddApi()
        {
            AddSupportButton();
            var endpoints = Endpoints();
            if (endpoints.Count == 0) { NoteRow("a:none", "No /help: the Lunar Link API module isn't running"); return; }
            // the module that serves it: a plugin's own endpoints come as "hot plugin" or "plugin" under its name
            static string Module(string group, string from) => from == "core" ? "Lunar Link" : from == "hot plugin"
                || from == "plugin" ? DisplayNames.For(group) : DisplayNames.For(from);
            static string Root(string path) => "/" + path.TrimStart('/').Split('/', '?', '.')[0] + "/";      // /help.html goes with /help
            foreach (var g in endpoints.GroupBy(e => (root: Root(e.path), module: Module(e.group, e.from))))
            {
                string key = "f:" + g.Key.root + g.Key.module; bool open = openFolder == key;
                FolderRow(key, g.Key.root, "- "
                    + g.Key.module, open,
                    () => { openFolder = open ? null : key; opened = open ? null : key; animateNext = true; Draw(true); });
                if (!open) continue;
                foreach (var e in g)
                {
                    var colour = e.from == "core" ? Ink : e.from.Contains("plugin") ? Accent : Green;
                    var help = OpeningRow("a:" + e.method + " " + e.path, e.method + " " + e.path, colour, null, Dim, Look.Live, out _);
                    if (help != null) RowText(help, e.help ?? "No help given", 0, 520, 13, Dim);
                }
            }
        }

        // ------------------------------------------------ Updates: the LunarLink.Update module's GET /update
        sealed class UpdateInfo
        {
            public bool on, busy, auto; public string running, state, latest, detail, error;
            public List<(string name, string state, string note, string built, string error, string version,
                string installed)> modules = new List<(string, string, string, string, string, string, string)>();
            public List<(string name, string kind, string version, string latest, string state, string from, string notes,
                string error)> plugins = new List<(string, string, string, string, string, string, string, string)>();
            // how many things have an update: the release, modules, plugins (the count on the tab)
            public int Waiting => (state == "available" ? 1 : 0) + modules.Count(m => m.state == "available") + plugins.Count(p => p.state == "available");
        }

        // on = false while that module is off
        static UpdateInfo ReadUpdates()
        {
            var u = new UpdateInfo(); var status = Updates.Status;
            if (status == null) return u;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(status()); var r = doc.RootElement;
                static string S(System.Text.Json.JsonElement e, string k) => e.TryGetProperty(k, out var x)
                    && x.ValueKind == System.Text.Json.JsonValueKind.String ? x.GetString() : null;
                u.on = true; u.auto = r.TryGetProperty("autoCheck", out var ac) && ac.ValueKind == System.Text.Json.JsonValueKind.True;
                u.running = S(r, "running"); u.state = S(r, "state"); u.latest = S(r, "latest"); u.detail = S(r, "detail"); u.error = S(r, "error");
                foreach (var m in r.GetProperty("channel").GetProperty("modules").EnumerateArray())
                    u.modules.Add((S(m, "name"), S(m, "state"), S(m, "note"), S(m, "built"), S(m, "error"), S(m, "version"), S(m, "installed")));
                if (r.TryGetProperty("plugins", out var ps))
                    foreach (var p in ps.EnumerateArray())
                        u.plugins.Add((S(p, "name"), S(p, "kind"), S(p, "version"), S(p, "latest"), S(p, "state"), S(p, "from"), S(p, "notes"), S(p, "error")));
                static bool Moving(string st) => st == "checking" || st == "downloading" || st == "reloading";
                u.busy = Moving(u.state) || u.modules.Any(m => Moving(m.state)) || u.plugins.Any(p => Moving(p.state));
            }
            catch (Exception e) { u.on = true; u.error = "couldn't read the updater: " + e.Message; }
            return u;
        }

        static string UpdatesSignature()
        {
            var u = ReadUpdates();
            return u.on + "|" + u.busy + "|" + u.auto + "|" + u.state + "|" + u.latest + "|" + u.detail + "|" + u.error + "|"
                + string.Join(",", u.modules.Select(m => m.name + m.state + m.error)) + "|"
                    + string.Join(",", u.plugins.Select(p => p.name + p.state + p.error));
        }

        static Color UpdateColour(string state) => state == "available" || state == "restart" ? Accent : state == "failed" ? Red
            : state == "up to date" || state == "updated" || state == "done" ? Green : Dim;

        // A row for Lunar Link's release, one per module on the channel and one per plugin that updates itself: what has
        // an update first, with Update on the row; + opens its details. Under the list: Check, and checking at game start.
        static void AddUpdates()
        {
            AddSupportButton();
            var u = ReadUpdates();
            if (!u.on) { NoteRow("u:off",
                "Updates are off: the Lunar Link Updates module isn't running (Show Lunar Link Modules on Hot Mods and Enable it)"); return; }
            bool restart = u.state == "restart", canAct = !u.busy && !restart;
            if (restart || CoreWaiting)                            // a new core is in: one restart loads it
                TitleButton(false, 0, restartAt > 0f ? "Saving..." : "Restart & Apply", 170, true, restartAt > 0f ? null : (Action)Restart);

            var rows = new List<(string key, string title, string state, Color colour, string detail, Func<string> update)>();
            string core = u.state == "available" ? u.latest + " available" : restart ? "restart to finish" : u.state == "failed" ? "failed"
                : u.state == "up to date" || u.state == "done" ? "up to date" : u.state == "idle" ? "not checked" : u.state + "...";
            rows.Add(("u:core", "Lunar Link " + u.running, core, UpdateColour(u.state),
                u.error != null ? "<color=" + RedHex + ">" + Short(u.error) + "</color>" : u.detail ?? "Check asks GitHub",
                u.state == "available"
                    ? () => { Updates.Apply?.Invoke("");
                    return "Updating Lunar Link to " + u.latest + "... (the new core loads at the next start)"; } : null));
            foreach (var m in u.modules)
            {
                var name = m.name;
                rows.Add(("u:m:" + name, DisplayNames.For(name) + (m.installed != null ? "  " + m.installed : ""),
                    m.state == "available" ? (m.version != null ? m.version + " available" : "newer build")
                        : m.state == "needs core" ? "needs a newer core" : m.state, UpdateColour(m.state),
                    m.error != null ? "<color=" + RedHex + ">" + Short(m.error) + "</color>"
                        : m.state == "needs core" ? "Comes with the next Lunar Link release"
                            : (m.note != null ? Short(m.note) + "   ·   " : "") + (m.built != null ? "built " + m.built : ""),
                    m.state == "available" && !restart
                        ? () => { Updates.Apply?.Invoke(name);
                        return "Updating " + DisplayNames.For(name) + "... (it reloads in place)"; } : null));
            }
            foreach (var p in u.plugins)
            {
                var name = p.name;
                rows.Add(("u:p:" + name, DisplayNames.For(name) + "  " + p.version,
                    p.state == "available" ? p.latest + " available" : p.state == "restart" ? "restart to finish" : p.state, UpdateColour(p.state),
                    p.error != null ? "<color=" + RedHex + ">" + Short(p.error) + "</color>"
                        : "from " + p.from + (p.kind == "hot" ? ", reloads in place" : ", needs a restart")
                            + (p.notes != null ? "   ·   " + Short(p.notes.Trim().Split('\n')[0].TrimStart('-', ' ', '*')) : ""),
                    p.state == "available" && !restart
                        ? () => { Updates.Apply?.Invoke("plugin:" + name); return "Updating " + DisplayNames.For(name) + "..."; }
                        : null));
            }
            foreach (var r in rows.OrderBy(r => r.update != null || r.state == "restart to finish" ? 0 : 1))
            {
                var details = OpeningRow(r.key, r.title, Ink, r.state, r.colour, r.update == null && r.state == "up to date"
                    ? Look.Off : Look.Live, out var header);
                if (r.update != null) RowButtons(header, Plus - 24f - Gap, ("Update", 90, true, () => Act(r.update)));   // one click, beside +
                if (details != null) RowText(details, r.detail, 0, 520, 13, Dim);
            }

            int newer = u.modules.Count(m => m.state == "available");
            bool all = newer > 1 && canAct;                       // Update all takes the room of the box's longer label
            var check = Box(0, all ? "Updates" : "Check for Updates");
            Button(check, u.busy ? "Checking" : "Check", new Vector2(BoxRight - 55, 0), new Vector2(110, 34), false,
                canAct ? () => Act(() => { Updates.Check?.Invoke();
                    return "Asking GitHub for Lunar Link's latest release and module builds..."; }) : (Action)null, 16, canAct);
            if (all)
                Button(check, "Update all", new Vector2(BoxRight - 55 - 118, 0), new Vector2(110, 34), true,
                    () => Act(() => { Updates.Apply?.Invoke("all"); return "Updating " + Plural(newer, "module") + "..."; }));
            Button(Box(1, "Check at Game Start"), u.auto ? "On" : "Off", new Vector2(BoxRight - 55, 0), new Vector2(110, 34), u.auto,
                () => Act(() => { Updates.Apply?.Invoke(u.auto ? "auto:0" : "auto:1");
                    return u.auto ? "Updates are checked only when you press Check"
                    : "Updates will be checked at every game start"; }));
        }
    }
}
