# The F9 Window

**F9** (the `Window key` setting, or **Lunar Link Keybind** in the window itself) opens and closes LunarLink's window in
the game. It has four tabs. Every list works the same way: each row opens with **+** (or a click anywhere on it) to
show its details and buttons, and closes with **-**; one row is open at a time, and the mouse wheel scrolls. Opening a
row scrolls just far enough to show what opened, never pushing the row itself out of view.

Along the top on every tab: **Support file**, which saves `BepInEx/lunarlink-support-<date-time>.zip` for a bug report
(see [[Troubleshooting]]), and a **?** that explains the tabs. The line at the foot says what the last button did.

## Hot Mods

One row per DLL in `BepInEx/hot/`: its name and version (a LunarLink module's number, or the one in a plugin's
`[BepInPlugin]`; **dev** for one built on your machine), and under it anything to know: its error in red, an orange
**newer build on disk**, **off from the next start**, or **wrapped (experimental)** for a normal plugin run by the
wrapper. Open, a row shows when the loaded build was made (or, wrapped, what the wrapper found it can't undo) and:

- **Enable / Disable** turns it on or off, and remembers that.
- **Reload** loads the build on disk now (orange when there's a newer one). A wrapped plugin that loads its own content
  (Mars Sweepers) can't be swapped under a running world, so in a world its reload **waits for the main menu**, and the
  row offers:
  - **Save & reload**: the game's own save, back to the main menu, the reload there, and your save loaded again by
    itself (when you're alone in your lobby);
  - **Save & menu**: the same, stopping at the menu (when anyone else is in your lobby, since leaving the world ends
    the session for them);
  - **Cancel**: no reload after all.
- **Settings**, when it has a config file: its settings on a page of their own (below).

At the top, **Rescan** picks up DLLs added to or removed from the folder, and **Reload changed** reloads every plugin
with a newer build on disk. Under the list, **Lunar Link Modules** shows or hides LunarLink's own modules (hidden by
default), and **Lunar Link Keybind** sets the window's key: press it, then the key you want (Escape keeps the old one).

Every button does exactly what the matching `/hot/*` request does.

## Mods

The normal plugins in `BepInEx/plugins/`, with their version and what needs them. Open, a row shows the file and:

- **Disable / Enable**, which take effect at the **next game start** (a loaded plugin can't be unloaded): the DLL is
  renamed to `.dll.disabled` and back. The row says "off from next start: restart the game to apply" until then;
  turning it back on before restarting needs no restart. A plugin another one needs can't be turned off, and LunarLink
  can't turn itself off.
- **Settings**, when it has a config file.

While a change waits for a restart (a plugin turned on or off, a setting changed, a plugin's DLL updated or a new one
copied in, or a new LunarLink core installed), **Restart & Apply** shows at the top right: it saves the game, closes it and starts
it again. On Windows Steam starts it; under Proton (Linux) LunarLink starts it with Proton's own wine once the old one
has closed, and writes what it did to `BepInEx/lunarlink-restart.log`. If it can't start the helper, the game stays open
and the foot says to restart it yourself.

## Settings pages

**Settings** on a row opens that plugin's settings in place of the list: its settings from its config file in
`BepInEx/config/`, under a heading per section, each with a short description. Click a value to change it: a switch
flips **On / Off**, a choice steps to the next one, anything else is typed (Enter saves, Escape cancels). The first
change keeps a copy of the file as `<name>.cfg.lunarlink.bak`.

A hot mod reads its config again when it reloads, so its page has **Reload** (orange once something changed): the new
settings are in use at once. A normal plugin's changes wait for the next start (**Restart & Apply** on Mods).
**‹ Back**, or any tab, returns to the list where you were.

## API

Every endpoint there is right now, LunarLink's and the plugins', in a folder per path: **/dust/ - Lunar Link API**,
**/quantum/ - Quantum Conveyors**. The name after the dash is the module or plugin that serves it. Open a folder for
its endpoints, and an endpoint for what it does. It reads `GET /help`, so it always matches what's running: a plugin
that's off has no folder.

## Updates

One row for LunarLink's release, one per module and one per plugin that says where its builds are (`UpdateFeed()`),
each with its version and where it stands: green **up to date**, orange **1.4.0 available** or **restart to finish**.
What has an update comes first, with an **Update** button on its row; open a row for its details (the version you have,
when it was built, where a plugin's updates come from). While anything has an update, the tab shows an orange count:
**Updates ●2**.

Under the list: **Check for Updates** asks GitHub for the latest release, the module channel and plugins' feeds (with
**Update all** when several modules have one), and **Check at Game Start** (off by default) checks once by itself each
time the game starts. Downloads are checked against GitHub's SHA-256. Modules and hot plugins reload in place; a new
core, or a normal plugin, needs a restart, and until then there's nothing to check or install. An update that reloads
the window itself reopens it on the same tab, so you see the result. See [[Installing]] and [[Updates]].

## Notes

- The player holds still while the window is open, and the mouse cursor is free; both go back when you close it
  (Escape, a right click, the **×** or the key again).
- The window doesn't react while you're typing in chat.
- The window is a hot module itself (`LunarLink.Window`): if a window build ever fails to load, `POST
  /hot/reload?name=LunarLink.Window` brings it back.
