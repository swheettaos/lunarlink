# Roadmap

## Replace the separate Mod Manager

Migrate the existing mod-manager settings UI and per-mod configuration handling into Lunar Link. The goal is to let
Lunar Link provide both hot-plugin management and mod settings, so Moon Sweepers can use Lunar Link alone instead of
requiring both Lunar Link and the separate Mod Manager plugin.

When this is implemented, preserve the current settings behavior for Quantum Conveyors, J.O.O.N.A.S. Joyride, and any
other supported mods, including defaults and saved values. The migration should be tested with the standalone Mod
Manager disabled before removing it from the recommended dependency list.
