# API Recipes

Whole jobs, step by step: which endpoints to call, in what order, and what to watch for. Every endpoint is in
[[API Reference]] with its parameters and a real answer. The examples use `curl`; from Python, the two helpers in
[[Using the API]] do the same.

Changes (every `POST` but `/…/check`) need the `allow-writes` file ([[Settings]]). Points are world coordinates: the
moon's centre is `0,0,0` and its surface is about 2,000 m out, so "up" at a point is simply away from the centre.

```bash
A=127.0.0.1:8765
```

With the [[MCP]] server, an AI assistant can run most of these for you: ask for the job ("film the north base from
above", "why did this base stop?") and it calls the same endpoints.

## Film or watch a base from a fixed angle

The observer camera stays where you put it while the player walks about, so the picture is steady even while a script
moves the player from spot to spot: for a timelapse of a build, watching a long job, or a screenshot from the sky.

```bash
curl -s $A/player                                                    # where you are (to pick a spot)
# a camera 40 m to the side and 40 m up, looking at the base's centre; hide=1: the game's camera stops drawing (cheaper)
curl -s -X POST "$A/camera/modify?x=40&y=2032&z=-40&tx=0&ty=1992&tz=-11&fov=55&hide=1"
curl -s -X POST "$A/message?text=Building%20the%20north%20row&ttl=0"  # a caption, until replaced
# ... record, or run your build script ...
curl -s -X POST "$A/message?text="                                   # caption off
curl -s -X POST $A/camera/remove                                     # the game's camera back
```

- Call `/camera/modify` again with new numbers to move the shot; a script can glide it a step at a time.
- The game's look-at info cards stay hidden while the observer camera is on, so they don't pop up in the picture.
- `GET /camera` says whether it's on and where.

## Build a structure from a script

```bash
curl -s "$A/kinds?all=1"                                             # pick a kind: name, price, footprint, height, ports
curl -s "$A/terrain?x=12&y=1992&z=-4&half=10&step=2"                 # is the ground flat enough there?
curl -s -X POST "$A/player/modify?x=12&y=1993&z=-4"                  # stand within ~22 m (building needs it)
BODY='{"kind":101,"pos":[12.5,1992.1,-4],"q":[0,0,0,1],"support":-1}'
curl -s -X POST $A/structures/check  -d "$BODY"                      # would it fit? nothing is spent
curl -s -X POST $A/structures/create -d "$BODY"                      # build it: answers its id
curl -s "$A/structures?since=45011"                                  # what was built since (the id before yours)
```

- Stacking: give `support` the id of the floor or post it stands on.
- `"ok": false` comes with the game's own `reason` (in the way, too steep, too far).
- To get exactly what a player's build tool would make (its snapping and stacking), drive it instead:
  `POST /builder/aim` with a kind and a ray, `GET /builder` to see if the preview is valid, then `POST /builder/place`.

## Lay a belt into a station

```bash
curl -s $A/kit                                                       # the rules: length, turn radius, slope, height
curl -s $A/stations                                                  # each station's intake sockets: pos and fwd
# a socket at pos [-9.811,1993.4,-8.798] with fwd [-0.446,0,0.895]: start 10 m out along fwd, run in against it
B='{"start":[-14.27,1993.4,0.15],"startDir":[0.446,0,-0.895],"end":[-9.811,1993.4,-8.798],"endDir":[0.446,0,-0.895]}'
curl -s -X POST $A/belts/check  -d "$B"                              # routable? length and price
curl -s -X POST $A/belts/create -d "$B"                              # build it (player within ~22 m)
```

- A socket's `fwd` points out of the station, so a belt arriving at it travels against it: `endDir` is `fwd` reversed.
- To connect to a machine or catcher, add `"startStructure"` / `"endStructure"` with its id; to continue a belt, add
  `"startBelt"` / `"endBelt"`.
- `GET /belts` lists every belt with its ends, to find one to extend.

## Find out why a base stopped

```bash
curl -s "$A/base/health?x=0&y=1992&z=-11&r=150"                      # only the real problems round the base
```

Then, for whatever it lists:

| It lists | Look at |
|---|---|
| `machines` (with the game's `status`) | `GET /production`: that machine's slots (starved of dust? output full?) and power |
| `catchers` with `outlet: false` | The belt out of that catcher: `GET /structures?x=&y=&z=&r=` round it, `GET /belts` |
| `robots` not cleaning | `GET /robots`: its `status` ("No dusty sector found nearby" means move it) |
| `shortGrids` | `GET /power`: supply against demand, battery left |

`"problems": 0` and `"ok": true` means the base round that point is working; idle machines are only counted, since a
press waiting for dust between capsules is normal.

## Clear dust from a region

```bash
curl -s $A/dust/regions > regions.json                               # large: every unfinished region
```

Each entry of `dusty` is `[index, x, y, z, left, started]`, where `x, y, z` is a direction from the centre: multiply
it by `radius` (in the same answer) for the point on the surface. Then sweep round it:

```bash
curl -s -X POST "$A/clean?x=1050.2&y=-1690.4&z=-77.1&r=8"            # the game's own cleaning, as the player sweeps
```

To send robots instead, `GET /robots` shows which are idle and where.

## Take down everything in an area

```bash
curl -s "$A/structures?x=0&y=1992&z=-11&r=30"                        # what's there: ids and kinds
```

Then remove them **one at a time, about 0.25 s apart** (removing many at once freezes the game):

```bash
for id in 44462 44463 44464; do curl -s -X POST "$A/structures/remove?ids=$id"; sleep 0.25; done
```

- The game's normal refund is paid; add `refund=0` not to. Robots (kind 99) are skipped unless you add `robot=1`.
- Belts: `POST /belts/remove?ids=` (refunded by the game itself).

## Measure what's eating the frame rate

```bash
curl -s -X POST "$A/perf/engine?on=1"                                # Unity's own counters: almost free
curl -s $A/perf > /dev/null                                          # start a window
sleep 15
curl -s $A/perf                                                      # fps and engineMs: scripts, physics, rendering, GPU
curl -s -X POST "$A/perf/engine?on=0"
curl -s "$A/census?top=20"                                           # what there's thousands of, and what runs every frame
```

- To time one suspect method: `GET /perf/methods?type=MoonCleaners.MoonWorld`, then
  `POST /perf/time?type=MoonCleaners.MoonWorld&method=StepProduction/1`, and read `/perf` again. Timers cost frame time:
  `POST /hot/reload?name=LunarLink.Perf` takes them all out.
- `GET /perf/fixes` shows the fps fixes; `POST /perf/fixes/modify` switches them, to compare with and without.

## Host a game

```bash
curl -s -X POST "$A/lobby/modify?name=Night%20Shift"                 # the name in the server list (host only)
curl -s $A/lobby                                                     # what Steam's list shows
curl -s $A/vehicles                                                  # the crew's vehicles, owners and prices
curl -s -X POST "$A/vehicles/remove?storage=g2-0&refund=1"           # take one away, its price back to the team
```

Plugins add their own: `GET /help?path=/crew` lists Crew Tools' (players, catches, kick, ban), if it's installed.

## Look inside the game's code

For modders, with no source code to read:

```bash
curl -s "$A/dev/xref?find=looked%20clean&type=MoonCleaners.MoonWorld"   # which method uses that text
curl -s "$A/dev/dis?type=MoonCleaners.MoonWorld&method=CleanLookingChecked"  # what it does, calls named
curl -s "$A/dev/fields?type=MoonCleaners.MoonWorld"                     # its fields and their offsets
curl -s "$A/dev/find?name=DevResetDust"                                 # a method by name: its class, live objects
```

`POST /dev/call?type=&method=` runs a parameterless game method on purpose; it does whatever that method does, so
read it first.

## Watch your plugin while you write it

```bash
./build.sh --hot MyPlugin                                            # build, copy into BepInEx/hot/, reload
curl -s "$A/hot/list?gc=1"                                           # running? error? leftovers? old copies freed?
curl -s "$A/help?path=/myplugin"                                     # its endpoints, as /help shows them
```

See [[Writing a Hot Plugin]].
