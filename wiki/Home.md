# LunarLink

<!-- include: README.md#intro -->

## Your first five minutes

1. **Install it** with the game closed: `./build.sh --install` ([[Installing]]).
2. **Start the game and load a moon.** LunarLink waits until the loading screen is gone.
3. **Ask it something:** `curl -s 127.0.0.1:8765/status` answers with the build and what's loaded.
4. **Open `http://127.0.0.1:8765/help.html`** in a browser: every endpoint there is right now, searchable.
5. **Press F9** in the game: the hot plugins, your normal BepInEx plugins and the same endpoint list.
6. **Want to build or remove things?** Create the `allow-writes` file ([[Settings]]), then try
   `POST /structures/check` before `POST /structures/create`.

## Start here

| If you want to | Read |
|---|---|
| Reload plugins without restarting | [[Hot Plugins]] |
| Make a plugin you have hot | [[Writing a Hot Plugin]] (Converting an existing plugin) |
| Write your own hot plugin | [[Writing a Hot Plugin]], with code in [[Plugin Examples]] |
| Install it | [[Installing]] |
| Install other plugins: which go in `BepInEx/hot/`, which in `BepInEx/plugins/` | [[Installing Plugins]] |
| Change the port, open it to another machine, allow changes | [[Settings]] |
| Call the API from a script or a browser | [[Using the API]] |
| Do a whole job (film a base, build by script, find what's wrong) | [[API Recipes]] |
| Let an AI assistant (Claude, Cursor...) use the game | [[MCP]] |
| Look up an endpoint | [[API Reference]] (or the searchable page on the [docs site](https://swheettaos.github.io/lunarlink/api.html)) |
| Use the in-game window | [[The F9 Window]] |
| Update LunarLink, or let your plugin update itself | [[Updates]] |
| Find out why the frame rate dropped | [[Performance Tools]] |
| Fix something that went wrong | [[Troubleshooting]] |
| Understand how it's built | [[How It Works]] |
| Report a bug or send a change | [[Contributing]] |

## At a glance

- Listens on `http://127.0.0.1:8765/` - this machine only, unless you choose an address and an access key.
- Reads are `GET`; anything that changes the game is `POST` and needs an `allow-writes` file, so nothing changes the
  game by accident.
- `GET /help` (or `/help.html` in a browser) lists every endpoint there is right now, plugins' included.
- Download it from [Nexus Mods](https://www.nexusmods.com/moonsweepers/mods/158) or the [latest release](https://github.com/swheettaos/lunarlink/releases/latest).
- Licensed under the [GPL-3.0](https://github.com/swheettaos/lunarlink/blob/main/LICENSE).
