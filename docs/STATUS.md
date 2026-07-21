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
| Native movement detours | 🟡 | AirAccelerate→FinishMove hooked (sigs + typed `MoveData`), ON by default, **pass-through** — physics fill + FinishMove vhook pending live validation. |
| Jumpstats | 🟡 | **Basic** — LJ/BH + distance tiers only. Missing full stat set, jump-type classification, invalidation, jumpstats DB. |
| HUD | 🟡 | Run timer + paused + speed/keys/mode/tp. Missing PB delta (needs cached PB), checkpoint count, spectator/replay HUD. |
| DB | 🟡 | Runs/BestRuns/TrackScores/Bans/Prefs. Missing: jumpstats table, startpos, course names. |
| Ranks | ✅ | Points + rank, ban-excluded leaderboards, `wr/pb/rank/top/recent/...`. |
| Global API | 🟡 | Submit-only client (hello + NewRecord). Missing: PB/top/WR queries, replay up/download, auth/Prime, ban enforcement. |
| Anticheat | 🟡 | 2 of cs2kz's 6 detectors (invalid-cvar + bhop-chain). No telemetry detectors, no infractions DB. |
| Ban management | ✅ | `!ban`/`!unban` (@kz/ban) + connect-time kick, persisted. |
| Preferences | ✅ | Mode/FOV/styles persist across reconnect (subset of cs2kz option keys). |
| Utilities | ✅ | `goto`, `fov`, `measure`, `pistol`, `tip`, `noclip`. |
| Localization | 🟡 | i18n infra live (ILocalizerManager + Loc helper + ChatFormat + `kreedz.json`); ModeModule converted. Remaining modules' strings convert module-by-module. |

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
