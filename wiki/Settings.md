# Settings

LunarLink's settings are in `BepInEx/config/lunarlink.cfg`, plus two files in `BepInEx/plugins/LunarLink/`.

## The server

| `[Server]` setting | Default | What it does |
|---|---|---|
| `Address` | `127.0.0.1` | Where the API listens. `127.0.0.1` is this machine only. This machine's address on your network (for example `192.168.1.20`), or `0.0.0.0` for every network, lets other machines in. An address that isn't this machine's falls back to `127.0.0.1`, and the log says so |
| `Port` | `8765` | The port |
| `Access key` | empty | What another machine must send to use the API: the header `X-LunarLink-Key: <key>`, or `?key=<key>`. With no key set, other machines are refused. Requests from this machine never need it |

Changes to `[Server]` apply at the next game start.

**Opening it to another machine.** The API can build, remove, kick and ban, so:

- pick a long random key (for example `openssl rand -hex 24`);
- only open it on a network you trust;
- prefer the header to `?key=`. The key is never written to the request log either way.

```bash
curl -s -H "X-LunarLink-Key: <key>" http://192.168.1.20:8765/status
```

## Allowing changes

Anything that changes the game - building, removing, teleporting, changing a setting, turning a plugin on or off -
needs the file `BepInEx/plugins/LunarLink/allow-writes` to exist. Without it those requests answer
`{"error":"writes disabled (no allow-writes file)"}`. Reading always works.

This holds for plugins' endpoints too: LunarLink passes a `POST` on to a plugin only when the file exists.

## The window

| `[Hot plugins]` setting | Default | What it does |
|---|---|---|
| `Window key` | `F9` | The key that opens and closes [[The F9 Window]] (an Input System key name) |
| `Show LunarLink modules` | off | Whether the window lists LunarLink's own modules among the hot plugins (also a tick box in the window) |

## Files LunarLink keeps

In `BepInEx/plugins/LunarLink/`:

| File | What |
|---|---|
| `allow-writes` | Its presence allows changes (see above) |
| `installed-version` | The stamp of the build `build.sh --install` put in |
| `requests.log`, `requests.log.1` | Every request as it starts and finishes, and the stuck-frame report (see [[Troubleshooting]]) |
| `power_fix.txt`, `offer_backoff.txt`, `audio_far.txt` | The fps fixes' settings (see [[Performance Tools]]) |
| `dust_geometry.json`, `chunk_names.txt` | Written and read by `GET /dust/geometry` |

In `BepInEx/hot/`, `hot.json` remembers which hot plugins are on (`enabled`) and which you turned off (`disabled`).
