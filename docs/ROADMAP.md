# Roadmap

## Replace the separate Mod Manager

Let Lunar Link provide both hot-plugin management and mod settings, so Moon Sweepers can use Lunar Link alone instead
of Lunar Link and the separate Mod Manager plugin.

**Done:** the F9 window edits any plugin's BepInEx config: **Settings** on a row of Hot Mods or Mods opens its
settings page (switches, choices, numbers, text and keys, checked against the allowed values and ranges, the first
change backing the file up). A hot mod applies its changes with Reload; a normal plugin with Restart & Apply.

**Left:**
- Check each supported mod's settings (Quantum Conveyors, J.O.O.N.A.S. Joyride, the rest) behave as in Mod Manager,
  defaults and saved values included, with the standalone Mod Manager turned off.
- Restart & Apply under Proton starts the game with Proton's own wine (Steam can't be asked while it still counts the
  game as running): try it on a Steam Deck and a desktop Linux before relying on it.
- Then take Mod Manager out of the recommended plugins.
