using System;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace LunarLink
{
    // A plugin's settings page, in place of the list (opened from its row's Settings), and typing: a setting's value, or
    // the key that opens the window.
    static partial class Window
    {
        sealed class SettingsPage { public string Title, Name, Guid, File; public bool Hot; public float ScrollBefore; }
        static SettingsPage page;

        static void OpenSettings(SettingsPage p)
        {
            p.ScrollBefore = scrollTarget; page = p;
            scroll = scrollTarget = 0f; animateNext = true;
            Draw(true);
        }

        static void CloseSettings()
        {
            if (page != null) scroll = scrollTarget = page.ScrollBefore;
            page = null; animateNext = true;
            Draw(true);
        }

        // A header (Back, the plugin's name, and for a hot plugin Reload: a reload reads its config again, so its changes
        // apply at once), then its settings, under a heading for each section when it has more than one. A normal
        // BepInEx plugin's changes wait for a restart (Restart & Apply on Mods).
        static void AddSettingsPage(SettingsPage p)
        {
            AddSupportButton();
            var settings = PluginSettings.For(p.Name, p.Guid, p.File);
            var header = AddRow("s:header", Look.Heading);
            Button(header, "‹ Back", new Vector2(-250, 0), new Vector2(90, 34), false, CloseSettings);
            Label(header, p.Title + "  <size=13><color=" + DimHex
                + ">settings</color></size>", new Vector2(-40, 0), new Vector2(300, 34), 16, Ink, TextAnchor.MiddleLeft);
            if (p.Hot)
            {
                bool changed = settings.Any(PluginSettings.Changed);
                RowButtons(header, Right, ("Reload", 84, changed, () => Act(() =>
                {
                    var result = Hot.Enable(p.Name, reload: true);
                    if (result != null && result.Error == null) PluginSettings.Applied(settings);
                    return Done(result, "Reloaded " + p.Title + (changed ? ": its new settings are in use" : ""));
                })));
            }
            if (settings.Count == 0) { NoteRow("s:none", "No settings found in its config file"); return; }
            bool sections = settings.Select(x => x.Section).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            string section = null; int index = 0;
            foreach (var setting in settings)
            {
                if (sections && !string.Equals(section, setting.Section, StringComparison.OrdinalIgnoreCase))
                {
                    section = setting.Section;
                    RowText(AddRow("s:section:" + section, Look.Off), "<b>" + section + "</b>", 0, 300, 14, Dim);
                }
                AddSetting(AddRow("s:" + index++, Look.Live), setting);
            }
        }

        // its name, a short description under it, and its value on a button: a switch flips, a choice steps to the next,
        // anything else is typed (Enter saves, Escape cancels)
        static void AddSetting(RectTransform row, PluginSettings.Entry setting)
        {
            bool described = !string.IsNullOrEmpty(setting.Description);
            RowText(row, setting.Key, described ? 10 : 0, 300, 14, Ink);
            if (described)
            {
                var d = setting.Description.Replace("\r", " ").Replace("\n", " ").Trim();
                RowText(row, d.Length > 54 ? d.Substring(0, 54) + "…" : d, -12, 320, 11, Dim);
            }
            bool typing = editing != null && editing.Id == setting.Id;
            string value = typing ? draft + "  |  Enter = save"
                : setting.Type == PluginSettings.Kind.Toggle ? (setting.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "On" : "Off")
                : setting.Value.Length > 22 ? setting.Value.Substring(0, 22) + "…" : setting.Value;
            var b = Button(row, value, new Vector2(Right - 75, 0), new Vector2(150, 34), typing, () =>
            {
                if (setting.Type == PluginSettings.Kind.Toggle) Act(() => PluginSettings.Toggle(setting));
                else if (setting.Type == PluginSettings.Kind.Choice) Act(() => PluginSettings.Next(setting));
                else { editing = setting; draft = setting.Value; message = "Type a value for " + setting.Key + ". Enter saves; Escape cancels."; Draw(true); }
            }, 13);
            if (typing) settingText = b.GetComponentInChildren<Text>();
        }

        // ------------------------------------------------ typing a value
        static PluginSettings.Entry editing; static string draft; static Text settingText;

        static void CaptureSetting(Keyboard kb)
        {
            foreach (Key k in Enum.GetValues(typeof(Key)))
            {
                if (k == Key.None) continue;
                var control = kb.get_Item(k);
                if (control == null || !control.wasPressedThisFrame) continue;
                if (k == Key.Escape) { editing = null; message = null; Draw(true); return; }
                if (k == Key.Enter || k == Key.NumpadEnter) { message = PluginSettings.Set(editing, draft); editing = null; Draw(true); return; }
                if (k == Key.Backspace) { if (draft.Length > 0) draft = draft.Substring(0, draft.Length - 1); }
                else if (Typed(k, kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) is char c && draft.Length < 120) draft += c;
                if (settingText != null) settingText.text = draft + "  |  Enter = save";
                return;
            }
        }

        // the character a key types (a US layout), or null
        static char? Typed(Key k, bool shift)
        {
            var key = k.ToString();
            if (key.Length == 1 && char.IsLetter(key[0])) return shift ? char.ToUpperInvariant(key[0]) : char.ToLowerInvariant(key[0]);
            if (key.Length == 6 && key.StartsWith("Digit", StringComparison.Ordinal)) return key[5];
            if (key.Length == 7 && key.StartsWith("Numpad", StringComparison.Ordinal) && char.IsDigit(key[6])) return key[6];
            switch (k)
            {
                case Key.Space: return ' ';
                case Key.Period: case Key.NumpadPeriod: return '.';
                case Key.Comma: return ',';
                case Key.Minus: case Key.NumpadMinus: return shift ? '_' : '-';
                case Key.Equals: return shift ? '+' : '=';
                case Key.NumpadPlus: return '+';
                case Key.Slash: return shift ? '?' : '/';
                case Key.Backslash: return shift ? '|' : '\\';
                case Key.Semicolon: return shift ? ':' : ';';
                case Key.Quote: return shift ? '"' : '\'';
                case Key.LeftBracket: return shift ? '{' : '[';
                case Key.RightBracket: return shift ? '}' : ']';
                case Key.Backquote: return shift ? '~' : '`';
                default: return null;
            }
        }

        // ------------------------------------------------ the window's key: a button that takes the next key pressed
        static bool capturingKey;

        static void KeyField(RectTransform box)
        {
            Button(box, capturingKey ? "Press a key" : KeyLabel, new Vector2(BoxRight - 55, 0), new Vector2(110, 34), capturingKey,
                () => { capturingKey = true; Draw(true); });
        }

        static void CaptureKey(Keyboard kb)
        {
            if (kb.escapeKey.wasPressedThisFrame) { capturingKey = false; Draw(true); return; }
            foreach (Key k in Enum.GetValues(typeof(Key)))
            {
                if (k == Key.None) continue;
                var control = kb.get_Item(k);
                if (control == null || !control.wasPressedThisFrame) continue;
                if (KeySetting != null) KeySetting.Value = k.ToString();
                SaveConfig();
                capturingKey = false; keyName = null;
                message = KeyLabel + " opens and closes this window now";
                Draw(true);
                return;
            }
        }
    }
}
