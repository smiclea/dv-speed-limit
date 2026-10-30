# Whole Train Speed Limit for Derail Valley — 0.3.2

Adds a compact **TRAIN LIMIT** readout above the controls in the **F4 driving UI**. It shows the lowest posted track limit beneath the coupled train, from leading coupler to rear coupler.

A higher limit waits until the rear clears its sign. A lower limit applies when the leading coupler reaches the sign. Short restricted sections between cars count. Current bogie positions support multiple locomotives, reversed vehicles, pushing, reversing, coupling changes, and loading midway through a restriction.

## Stable reading during background work in 0.3.2

After the first resolved limit, brief background indexing or profile preparation keeps that last numeric limit and detail visible. It updates as soon as a new calculation completes. Overspeed coloring continues to follow current speed against the displayed limit. Initial loading still shows the yellow scanning message when no resolved limit is available. Genuine errors, neutral direction, locomotive/consist/direction changes, cache clearing, hiding F4, or a reset of the tracked route clear the held value. This changes presentation only; scanning, caching, and update frequency are unchanged.

## Junction lookup fix in 0.3.1

The 0.3.0 gameplay log confirmed a working numeric limit for a 19-car / 304 m train, followed by `Previous speed sign lies beyond an ambiguous junction`. Current-footprint junction links were being discarded when the rear cleared them, even though a backward lookup still needed the actual incoming route.

Version 0.3.1 retains up to 2,048 directed links observed beneath this train. A previous-sign lookup first uses the current footprint, then validated observed connections, then a single unambiguous physical connection. Changing a switch behind the train cannot replace the route actually observed. No guessed past switch selection is stored. This small route history is session-only and independent of the persistent sign cache.

History resets on locomotive/consist/direction/world changes, possible teleportation, an unavailable train footprint, cache clearing, or hiding F4. A junction never observed in the current tracking context can still display unavailable; the error now logs the exact track, span, orientation, branch count, and remembered-link count. Your existing area cache is preserved by this update.

## Persistent area cache

Speed signs are now stored in `Mods/DVTrainSpeedLimit/Cache/signs.xml` and reused on later visits and game launches.

- Each loaded area is scanned only if its stable streamer/scene key is absent from the cache.
- Newly loaded, uncached areas are discovered automatically through the game's `Streamer.LoadedScenesChanged` event. The mod checks native loaded-region records, rather than periodically searching the world.
- Cached areas, including areas containing no speed signs, are not scanned again.
- Sign coordinates survive scenery unloading. Area caches grow as you travel.
- **Clear entire sign cache** in UMM settings discards all cached areas and signs, deletes the saved data, and queues the currently loaded areas afresh. Later uncached areas populate automatically.
- Cache format and game-assembly build fingerprints are checked on load. Incompatible or corrupt data is discarded and rebuilt. Disk reads/writes use a background worker. Replacements are atomic; cache deletion and later writes are ordered so an old in-flight write cannot restore data after clearing.

Settings show the number of cached areas and signs. An initial cache covers loaded areas; it is not claimed to contain the entire map before exploration. No limit is inferred solely from cache size. Missing preceding sign data or geometry produces an unavailable readout.

## Why 0.2.0 stayed at “Indexing … 67 signs”

The runtime log confirmed that baked speed poles were found, but the hierarchy traversal never reached its completion gate during that session. Train calculations were blocked until the whole traversal finished.

Version 0.3.0 removes that object-by-object walk. It queries each uncached area's `SignGeneratorData` components directly and parses the resulting finite array. Decoration counts and newly created scene objects cannot keep extending that array. The native streamer exposes stable area names and their root objects, so already cached areas can be skipped before querying components.

The earlier 0.1.0 implementation expected a global sign generator that is absent during gameplay. Neither 0.2.0 nor 0.3.0 regenerates native signs or depends on that object. Original failure logs are preserved under the project's `dist/diagnostics/`.

## Use

Start/restart the game, enter a locomotive, and open **F4**. The first opening can briefly show `Loading sign cache`, `Indexing areas`, and `Reading track limits`. Later launches reuse saved areas. A changed game build rebuilds the incompatible cache.

The default font is restored to **20-point main text / 15-point details**, with a 350 x 68 panel. A new font-size slider in UMM allows 12–28 points; detail text and panel size scale with it. When the front has reached a faster section, the detail says `Front 90 | Rear restricted`, for example. Otherwise it shows car count and occupied length. Text turns red when speed is more than 0.5 km/h above the train limit.

Direction follows actual motion above 0.15 m/s, with hysteresis down to 0.03 m/s. At rest, it follows the reverser; neutral asks for forward/reverse. Changing the reverser while rolling does not immediately switch the directional signs used.

Open **Ctrl+F10 → Whole Train Speed Limit** to adjust font size/placement, hide the readout, inspect coverage, enable verbose logging, or **Clear entire sign cache**. Settings save through UMM. Disable the mod to remove its readout.

## Performance

Normal position/limit checks run five times per second with F4 open. With F4 closed, train calculations and area processing pause; streamer notifications merely mark the loaded-area list as changed.

Streamer discovery is a single typed lookup per world/session. Area component queries occur only for uncached areas. Between those native queries, parsing targets 0.5 ms per frame with at most four area jobs and 64 components per parsing pass. **A native component query cannot be interrupted**, so a first-time area query can exceed that target. Its duration and the largest observed query are logged. This replaces the previous managed hierarchy walk; it is not a promise of a hard 0.5 ms maximum.

Sign-to-track matching runs on one background worker. The Unity thread never waits for it. Both directional profiles are cached together. Disk serialization also runs off the Unity thread; no file operation is part of the normal position check. Newly cached empty/overlapping areas do not invalidate existing directional profiles.

Trainset layout is cached until consist/locomotive changes. Same-track bogie pairs use direct spans; gaps crossing tracks are searched only when their bogies change tracks. Contiguous bogie ranges on one track merge before limit lookup. Ordinary updates do not generate native track geometry or reconstruct signs. Persistent failures back off for two seconds; repeated identical error stacks are throttled to 30 seconds.

The 0.3.0 gameplay log measured a normal cached check at 0.22 ms and a profile-completion check at 0.61 ms for the 19-car train. These samples are not a worst-case FPS/memory benchmark. The standalone 20 km / 4,000-sign cold-profile workload is a .NET 8 worker benchmark, not a measurement of Unity/Mono frame time. Game logs record successful update durations, region query durations, worker matching time, and cache progress. Bounded observed-route history adds dictionary lookups without additional scanning or native geometry generation.

## Data and compatibility

The game exposes trainset membership, couplers, ordered bogies, bogie rail/span/orientation, and track connections. The mod reconstructs the short occupied route between adjacent bogies using possible connections within a distance bound. It can follow the branch actually occupied even when a switch was changed behind the train. Outer body overhang follows the native selected connection; a body may extend beyond the final rail at a buffer stop.

Baked speed-pole numbers are multiplied by ten. Pole position/facing and the native two-metre sideways placement offset match signs to the rail point sets used by bogies. Matching rejects parallel-track, elevated-track, and perpendicular signs. Dual-speed junction poles retain native outgoing-branch order; occupied branch information takes precedence over current switch selection. Mainline signs are directional. Unsigned `[Y]` yard tracks use 30 km/h; `[#]` tracks are not assumed to be yards.

Before a track's first sign, lookup walks backward to the preceding directional sign. Ambiguous junctions, loops, 128 tracks, or 10 km stop the lookup. Unavailable sign/geometry data displays `--` instead of retaining a stale number.

The target is the standard map and native baked signs. Custom placement offsets/maps, moving turntables, derailed/teleporting cars, and ambiguous geometry are not verified. This is an infrastructure speed-limit display, not automatic braking or a guarantee against derailment. It excludes vehicle/cargo-specific restrictions and the 5 km/h coupling rule. See the [official speed-limit manual](https://manual.derailvalley.com/wiki/Speed_Limit_Signs/en).

## Logs

- `Mods/DVTrainSpeedLimit/Logs/speed-limit.log`; previous session/rotation: `speed-limit.previous.log`.
- `DerailValley_Data/Managed/UnityModManager/Log.txt`, prefixed with `DVTrainSpeedLimit`.

The dedicated log flushes written messages and rotates at 5 MiB. Startup/cache messages include `Persistent sign cache loaded`, `Area cached`, `Area indexing progress`, and `Sign cache ready`. Subsequent diagnostics include `Baked track profile` and `Limit:`. An unchanged valid state is summarized every 30 seconds. Verbose mode adds sign/branch positions and five-second bogie/occupied-range snapshots.

If the number remains unavailable, retain the log after reproducing once. The progress line includes completed/pending area counts and the current area, so a failure can be located without another silent indexing stall.

## Build and install

Requires a .NET 8 SDK, the installed game, and Unity Mod Manager. Targets .NET Framework 4.7.2 against local game assemblies. Proprietary DLLs are not packaged. No NuGet or Harmony dependency.

```bash
bash scripts/build.sh
bash scripts/install.sh
```

Both scripts accept a game directory as their first argument or use `DV_GAME_PATH`. Set `DOTNET` if the SDK is not on PATH. The temporary `/tmp/dv-dotnet/dotnet` used during development is also recognized; it disappears when `/tmp` is cleared.

The build runs regression checks and produces `dist/DVTrainSpeedLimit-0.3.2.zip`. Installation replaces this mod's DLL, manifest, and README in `Mods/DVTrainSpeedLimit`, preserving settings, logs, and cache. The previous installation is backed up under `dist/backups/`. Restart to load a new DLL. To uninstall, close the game and remove that mod directory.

## Verification

Compiles against the installed game with zero warnings. The 76 checks cover speed boundaries, occupied paths, loops/disconnections, baked sign projection/direction, parallel/elevated-track rejection, floating-origin coordinates, background execution, 1,000-range compaction, finite 67-sign area completion, persistence across sessions, uncached-area scheduling, duplicate/empty-area handling, clearing, game-build invalidation, atomic replacement, observed junction links after rear clearance, changed switch selection, coordinate reversals, latest-route updates, stale/disconnected-link rejection, context clearing, and bounded history size.

The area cache and numeric readout were observed working in the 0.3.0 gameplay log. The 0.3.1 history fix and larger font still require an in-game check: repeat the junction departure until the rear clears, then check reverse travel, coupling changes, hiding/reopening F4, and font adjustment. The log should continue to report `Limit:` after rear clearance instead of losing the known incoming branch.
