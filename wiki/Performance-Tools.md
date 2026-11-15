# Performance Tools

The `LunarLink.Perf` module measures where a frame goes and carries a few fps fixes. It patches the game, so it's a
module of its own: reloading or turning it off takes every timer and fix out without touching the rest of the API.

## Measuring lightly: Unity's own counters

Unity counts its own frame time (whole frame, scripts, physics, rendering, GPU wait, draw calls) without patching
anything. Turn the counters on, read once to reset, wait, read again, and turn them off:

```bash
curl -s -X POST "127.0.0.1:8765/perf/engine?on=1"
curl -s 127.0.0.1:8765/perf > /dev/null      # reset the window
sleep 15
curl -s 127.0.0.1:8765/perf                  # fps, and engineMs: Unity's counters averaged over the 15 s
curl -s -X POST "127.0.0.1:8765/perf/engine?on=0"
```

This is the measurement to use while you watch the frame rate: it costs almost nothing.

## Timing methods (heavier)

| Request | Does |
|---|---|
| `POST /perf/timers` | Stopwatches on the game's and MoonFramework's main per-frame systems |
| `GET /perf/methods?type=X` | The methods a class declares, to pick one to time |
| `POST /perf/time?type=Full.Name&method=Name/paramCount` | A stopwatch on one method |
| `GET /perf` | Milliseconds per frame of everything timed, since the last read |
| `GET /census?top=40` | Every component class counted, and which have per-frame methods |

Timers patch the methods they time, and each costs a little every frame (some run thousands of times a frame: a timer
on a per-capsule belt method took a big base from 34 fps to 6 while it was in). Use them to hunt one specific cost, then
take them out with `POST /hot/reload?name=LunarLink.Perf` (or turn the module off).

Timers count only the main thread's time in `msPerFrame`. The game runs some work on its own worker threads (it builds
the dust meshes there, `MoonDustBankGeometry.UpdatePages`); a timed method's time on a worker shows apart, in
`backgroundMs`, because the frame doesn't wait for it.

The engine counters also split the rest of Unity's frame: waiting for the frame rate limit (the game caps itself at 30
fps while its window isn't in front, so measure with the game in front), audio, UI canvases, renderer updates,
particles and the game's garbage collector.

## The fps fixes

Each fix has its own on/off switch, and the ones with a value have that too. Change them in **F9 → Hot Mods → Show →
Lunar Link Performance → Settings**, in `BepInEx/config/LunarLink.Perf.cfg`, or over HTTP; every change is in use at
once (the module reads the file again within a second of it changing).

| Fix | What it does | Settings | Over HTTP |
|---|---|---|---|
| Power link | Replaces MoonFramework's once-a-second power linking with a spatial-grid version (about 80 ms to under 1 ms per run on a big base) | Fast power linking | `POST /perf/fixes/modify?name=power&on=1` |
| Offer back-off | A capsule refused at a full MoonFramework line waits N frames before it's offered again (most offers at full lines were refusals) | Back off refused offers, Back-off frames | `POST /perf/fixes/modify?name=power&backoffOn=1&backoff=4` |
| Distant audio | Machine sound loops further than N metres stop (hundreds of distant machines could run the game out of audio voices) | Silence far machines, Silence beyond metres | `POST /perf/fixes/modify?name=audio&on=1&m=60` |

`GET /perf/fixes` shows each fix's state and counters.

## What tends to cost the most

On big bases the frame is usually spent in per-frame loops over many machines (MoonFramework's driver), the dust mesh
rebuilding near many robots, shadows, and the belt simulation. Running the game's own production or conveyor step less
often does **not** help: the game runs fixed sub-steps, so the same work happens anyway.
