# KZ Port — Status

A reimplementation of [KZGlobalteam/cs2kz-metamod](https://github.com/KZGlobalteam/cs2kz-metamod) built
on a fork of [Source2Surf/Timer](https://github.com/Source2Surf/Timer). The **core loop is built and
green** (timer, checkpoints, modes, styles, DB, ranks, bans); it is **not yet 1:1** — cs2kz has 31
subsystems and several are partial or not started (see below). See `KZ_PORT_PLAN.md` for architecture and
`EXTENSIBILITY.md` for the mode/style plugin split.

## Subsystems

| System | State | Notes |
|---|---|---|
| Timer | 🟡 | PRO/STANDARD + start-validation gate (alive + Walk movetype). Missing: named courses (mapping-API), split zones, debounce guards, global submission/announce flow. |
| Checkpoints / teleports | ✅ | `cp/tp/undo/prevcp/nextcp/setstartpos/clearstartpos`. Startpos not DB-persisted. |
| Modes | 🟡 | External `Kreedz.Mode.VNL`/`.CKZ` via `IKzModeRegistry`. **Full 33/33 convar layer** now; registry still has no movement-callback API (3rd-party modes can't add custom physics hooks yet). |
| Styles | ✅ | 6 external plugins (`ABH,LGJ,LowGrav,Ice,WSOnly,ADOnly`) ≥ cs2kz's shipped set. |
| Native movement detours | 🟡 | Full AirAccelerate→FinishMove surface hooked (18 sig detours + FinishMove vtable hook). **All CKZ-override physics FILLED:** prestrafe, perf, rampbug (CategorizePosition), AirMove cap, **TryPlayerMove slopefix** (bump loop + 3×3×3 pierce search + ClipVelocity, applied only on rampbug detection). Non-overridden funcs correctly pass-through. Two flags: `kz_ckz_native_hooks` (default on — safe fills), `kz_ckz_tpm` (default **off** — the collision reimpl, enable after demo validation). Remaining: tick-for-tick demo validation on a live server. |
| Jumpstats | 🟡 | External `Kreedz.Jumpstats`. **Core stat set** (strafes/sync/gain/maxspeed/height) + **full jump-type classification** (LJ/BH/MultiBhop/WeirdJump/LadderJump/Ladderhop/Fall). Missing: edge distance, native duckbug precision, invalidation, jumpstats DB. |
| HUD | 🟡 | External `Kreedz.Hud` plugin (reads `IKzRunService`+`IKzModeRegistry`). Run timer + paused + CP/TP + speed/keys/mode. Missing PB delta (needs cached PB), spectator/replay HUD. |
| DB | 🟡 | Runs/BestRuns/TrackScores/Bans/Prefs. Missing: jumpstats table, startpos, course names. |
| Ranks | ✅ | Points + rank, ban-excluded leaderboards, `wr/pb/rank/top/recent/...`. |
| Global API | 🟡 | External `Kreedz.Global` plugin (submit-only: hello + NewRecord via `IKzRunService.RunFinished`). Missing: PB/top/WR queries, replay up/download, auth/Prime, ban enforcement. Live-gated (needs a real key). |
| Anticheat | 🟡 | External `Kreedz.Anticheat`. **4 detectors:** invalid-cvar, bhop-chain, nulls (tick), **snaptap (subtick — via `MoveData.SubTickMoves`)**. Missing: hyperscroll/strafe-optimizer, infractions DB. |
| Ban management | ✅ | `!ban`/`!unban` (@kz/ban) + connect-time kick, persisted. |
| Preferences | ✅ | Mode/FOV/styles persist across reconnect (subset of cs2kz option keys). |
| Utilities | ✅ | `goto`, `fov`, `measure`, `pistol`, `tip`, `noclip`. |
| Localization | ✅ | i18n infra + **all 10 modules' interactive messages** localized (40 keys). Only tip rotating-content strings remain inline (minor). en-US shipped; other cultures fall back. |

## Not started (missing subsystems vs cs2kz)

- **Mapping API + KZ trigger system** — the biggest gap. Model + parser now built (`Modules/MappingApi/`,
  verbatim from cs2kz, verified) but **not yet wired to a live keyvalue source**: ModSharp exposes no
  managed spawn-keyvalue read, so the source (native `CEntityKeyValues` detour vs. offline entity-lump
  extract) is a pending decision. Until wired, modern keyvalue-driven kz_ maps still register no zones.
- **Localization** — all output is hardcoded English; cs2kz has a ~30-language phrase system.
- **saveloc/loadloc**, **quiet (!hide/!hidelegs)**, **beam trails**, **paint**, **ztopwatch** (2-zone practice
  stopwatch), **profile** (rank titles/clan tag), **spec-by-name/speclist**, **racing/1v1**, **telemetry**.

## Live-gated (need a live CS2 server or an issued key — not doable headless)

1. **CKZ native physics fill** — detours are pass-through; rampbug/slopefix, exact air-accel/ladder physics
   need tick-for-tick validation vs demos on a real server.
2. **Official global submission** — needs a real key from KZGlobalteam (their backend checksum-validates the
   plugin). Client is built; local ranking runs regardless.
3. **Anticheat tuning** — the telemetry detectors need real movement data to calibrate.
