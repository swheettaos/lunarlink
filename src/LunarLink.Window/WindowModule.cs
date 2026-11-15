// LunarLink.Window: the window a key opens and closes (F9 by default, "Window key" in LunarLink's config). It's a hot
// module, so it reloads like any other. Four tabs:
//   Hot Mods      the DLLs in BepInEx/hot/: Enable, Disable and Reload (Save & reload for a wrapped plugin that loads
//                 its own content), their settings, Rescan and Reload changed
//   Mods          the normal plugins in BepInEx/plugins/ (BepPlugins): on or off from the next start, their settings,
//                 Restart & Apply
//   API           every endpoint (GET /help), a folder per path
//   Updates       Lunar Link's releases, its modules and plugins that update themselves (the LunarLink.Update module)
// Drawn in the look of Quantum Conveyors' menus with Unity's own UI, on a canvas made when the window opens and destroyed
// when it closes. Buttons act on the mouse press, found by a raycast (no onClick listeners: they don't survive reloads).
//
// Window.cs          opening and closing, input, the player frozen while it's open, running a button's action
// Window.Pane.cs     the pane: title, tabs, the list (rebuilt, scrolled and animated), the boxes under it
// Window.Parts.cs    rows, buttons, labels, the "?" tips and the sprites they're drawn with
// Window.Tabs.cs     what each tab lists
// Window.Settings.cs a plugin's settings page, and typing a value or a key
// Window.Restart.cs  Save & reload (a wrapped plugin's reload at the main menu) and Restart & Apply
namespace LunarLink
{
    public class WindowModule
    {
        // what a reload hands over: whether the window was open and on which tab (a "#window" line), then the BepInEx
        // plugins turned on this session; an update that reloads this module reopens the window where it was
        public void Start(string state) { BepPlugins.TakeOver(state); Window.TakeOver(state); BepPlugins.Routes(); }
        public string Stop() { var open = Window.Handover(); Window.Stop(); return open + BepPlugins.Handover(); }
        public void Update() => Window.Update();
        public void LateUpdate() => Window.LateUpdate();
    }
}
