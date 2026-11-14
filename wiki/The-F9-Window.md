# The F9 Window

**F9** (the `Window key` setting) opens and closes LunarLink's window in the game. It has four tabs; it's the same size
on each, and the mouse wheel or the scroll bar on the right moves through long lists.

## HOT PLUGINS

One row per DLL in `BepInEx/hot/`: its name, state (green running, red failed, grey disabled), when the loaded build
was made, an orange **newer build on disk** mark, and the error if it failed.

- **Enable / Disable** turns a plugin on or off (and remembers it).
- **Reload** loads the build on disk now.
- **Reload changed** (top) reloads every plugin with a newer build on disk; **Rescan** picks up DLLs added or removed.
- **Show LunarLink modules** shows LunarLink's own modules too (hidden by default).

Every button does exactly what the matching `/hot/*` request does.

## BEPINEX PLUGINS

The normal plugins in `BepInEx/plugins/`, with version, file and what needs them. **Disable** and **Enable** take effect
at the **next game start** (a loaded plugin can't be unloaded): the DLL is renamed to `.dll.disabled` and back. The tab
says so plainly - the state turns orange ("off from next start"), the row says "restart the game to apply", and a banner
counts the pending changes. Turning a plugin back on before restarting needs no restart.

A plugin another one needs can't be turned off, and LunarLink can't turn itself off.

## UPDATES

LunarLink's own updates. **Check for updates** asks GitHub for the latest release and the module channel; **Check for
updates at game start** (off by default) makes it check once by itself each time the game starts. The LunarLink row shows whether there's a newer release, and **Update** installs it; below it, one row
per module shows whether there's a newer build from `main` (**Update** reloads it in place; **Update all modules** at
the top does every one), and one per plugin that says where its builds are (`UpdateFeed()`): its version, the newest,
and where it comes from. A hot plugin reloads in place; a normal one needs a restart. While anything has an update,
the tab's name shows an orange count: **UPDATES ●2**. The download is checked against
GitHub's SHA-256. If only the modules changed they reload in place; if the core changed, the row says **restart to
finish**. See [[Installing]].

## API

Every endpoint there is right now, LunarLink's and the plugins', with what each does. **Showing: All ›** steps through
the areas and plugins one at a time. It reads `GET /help`, so it always matches what's running.

## Notes

- The mouse cursor is free while the window is open, and goes back to how it was when you close it.
- The window doesn't react while you're typing in chat.
- The window is a hot module itself (`LunarLink.Window`): if a window build ever fails to load, `POST
  /hot/reload?name=LunarLink.Window` brings it back.
