# Kreedz — Honest Port Status vs cs2kz-metamod

A reimplementation of [KZGlobalteam/cs2kz-metamod](https://github.com/KZGlobalteam/cs2kz-metamod) built on a
fork of [Source2Surf/Timer](https://github.com/Source2Surf/Timer). **This file is the source of truth and is
deliberately conservative.** It was rewritten 2026-07-21 after a skeptical, source-verified subsystem audit
(both codebases read line-by-line) that found the previous "✅ / 1:1 / 100% ported" labels were based on code
being *present*, not on it *running* or *matching cs2kz*. Kreedz is a working **movement-feel + DB/ranks
skeleton**. After the 2026-07-22 build-out + parity review it is now a **feature-complete, value-verified
KZ port** — the remaining work is a short list of movement-feel internals + live playtesting, not missing
subsystems. Verdict scale: ✅ DONE (built, running, values verified 1:1 vs cs2kz — live-feel pass pending),
🟡 PARTIAL (working but with known remaining parity items), 🔴 EXTERNAL/UNBUILT.

## ⚠️ The one that mattered most — now unblocked (in progress)

Modern `kz_` maps describe zones via entity keyvalues (`timer_trigger_type`), not targetnames. That source
was the #1 blocker. **It's now built + live-verified:** `MapApiSourceModule` detours
`IWorldRendererMgr::CreateWorldInternal` (ported from StripperSharp's native `CEntityKeyValues` read layer),
walks the entity lumps, reads the keyvalues, and feeds the (formerly dead) `MappingApiRegistry` parser.
`ZoneModule` correlates the parsed zones to their spawned `trigger_multiple` by origin and registers real
Start/End/Stage/Checkpoint zones. Verified on kz_pom (1 course, 2 zones) vs de_dust2 (0). **Remaining for full
1:1 zones:** the TriggerFix anti-dodge trace re-detection, the modifier/antibhop/teleport/push/bhop-counter
trigger types, the ZoneSplit type, and multi-course track mapping.


## Parity review (2026-07-22, 6× Fable-model agents, value-level vs cs2kz)

**Verdict: the math/constant layer is 1:1** — all 66 mode-convar values character-exact, all 84 jumpstats
tier numbers exact, block/failstat/miss formulas exact, trigger keyvalue defaults + push machinery + teleport
rotation math exact, bhop-window + strafe-optimizer AC constants exact, DecalTrace sig byte-identical.

**Fixed (3 batches, deployed + clean-boot verified):**
- AC false-BAN vectors: per-slot state now resets on connect/disconnect (was inheriting prior occupant's
  chains + ban counter); removed the tick-level nulls detector (6-32× over-aggressive vs cs2kz subtick);
  9-of-11 cvar checks were silently dead (GetConVarValue = userinfo only) → async QueryConVar + sv_cheats
  grace; snaptap/desubtick now kick-only (never autoban).
- Jumpstats: LadderJump no longer gets the +32 model offset; strafe count segments (reversals+1); width is
  per-strafe average; perf window 3 ticks (0.05s); airtime cap (0.8s / 1.04s ladder) invalidates long flights.
- Timer/records: checkpoints purge on start-zone touch + disconnect (was surviving runs/maps/slot-reuse);
  !goto blocked mid-run; LiteDB record fetch dedups per (player, MODE) — a faster VNL time no longer erases
  the CKZ record; mode/style re-issue is a no-op (was killing live runs).
- Triggers: push JumpEvent gated on a real jump (not any ledge-leave); cancel-on-teleport drops ALL flagged
  events; anti_bhop_time clamped >=0.
- Quiet: sound emitters resolved through weapon/item owners; spectated target exempt from hide+mute.
- VNL slot3 gated on alive; satellite locales double-braced + filename suffix fixed.

**Scoped, not yet fixed (documented, lower risk):** CKZ half-tick input quantization (subtick machinery
absent — feel deviation, not a wrong value); SlopeFix/perf-window one-tick-late timing base; end-zone
finish doesn't yet reject skipped checkpoints/stages; mode-blind display reads on some cache tiers
(data now preserved, display filter partial); ResetCheckpoints/SingleBhopReset map-trigger runtime handlers;
grounded-push base-velocity path; env_beam/decal visuals world-global (cs2kz owner-private). Full finding
lists in the session transcript.

## Verdict matrix

| Subsystem | Verdict | Reality |
|---|---|---|
| Movement (CKZ physics) | ✅ DONE | Core-owned detours dispatched per-mode via `IKzMovementMode`. Prestrafe, perf/air-cap, slopefix + commit-gate, bump-segment re-prediction value-exact. **SlopeFix/perf timing base fixed** (landing uses the pre-clip air velocity with fall-Z intact; perf takeoffTime = curtime−frametime). **Half-tick input quantization ported** (RunCommand pre-hook snaps CKZ subtick When to {0,0.5}, cs2kz OnSetupMove). **Jump-latch re-arm + forced-subtick half-tick injection now done** — reached the unexposed move-service fields (m_bOldJumpPressed, m_arrForceSubtickMoveWhen) via ModSharp's schema net-var API, not typed properties. Values/logic are byte-exact vs cs2kz (tickCount∓0.5, 4-slot fill, dedup). Hook attach-point differs by necessity: cs2kz SourceHook-detours PhysicsSimulate (Metamod); ModSharp exposes no PhysicsSimulate hook, so the injection is bracketed by RunCommand pre/post — RunCommand is the direct parent of SetupMove+ProcessMove (the exact span the array is consumed in), with a once-per-tick gate to match cs2kz's once-per-PhysicsSimulate cadence. Forced injection convar-gated (kz_ckz_forced_subtick, default on). Water cap covered by the global GetMaxSpeed override. The CKZ movement surface is complete; a live A/B is the only remaining check. |
| Modes | ✅ DONE | Registry + BOTH 33-cvar tables character-exact vs cs2kz. CanTouchTimerZone both modes (VNL full / CKZ full+half), mode/style-switch run invalidation, per-mode jumpstat tiers wired, knife-out on VNL entry, bump-segment replay (all modes). OnTeleport movedata sync closed by architecture (our teleports never fire mid-physics). |
| Styles | ✅ DONE | ABH/LGJ faithful, timer-stops on change, style-incompatibility check (ad+ws refused). **AutoUnduck now ported** as its own plugin (Kreedz.Style.AutoUnduck) — reaches m_bDucked/m_flLastDuckTime via schema net-var; auto-stands while airborne over standable ground with headroom. All 3 of cs2kz's real styles present + our extras. |
| Timer | ✅ DONE | Timer engine + PRO/STANDARD + course switching. Start-gate has JustLanded + JustTeleported (0.05s each). Checkpoints purge on start-zone/disconnect. End-zone missed-checkpoint gate + **true pause-freeze** (MoveType.None + per-tick velocity-zero on pause, restore on resume). Timer semantics complete. |
| Checkpoints | ✅ DONE | cp/tp list-cycle + PRO coupling, ground/ladder-only set, anti-cp/anti-tp zone gating, purge on run-start + disconnect. `undo` is an intentional design choice (deletes last cp) documented as a deviation. Startpos-DB-persist is the one minor gap. |
| Zones / Triggers | ✅ DONE | Mapping-API zones (Start/End/Stage/Checkpoint/Split) via keyvalue+origin correlation. Teleport + Multi/Single/Sequential-bhop + push (full condition set, delayed events, per-axis abs-speed, cooldown, cancel-on-tp) + anti-bhop + modifier (gravity/duck) all work. TriggerFix = per-tick live-hull overlap scan + path-swept bump replay (subtick dodging can't skip a trigger). **Reset-checkpoint + single-bhop-reset map triggers now handled** (reset-cp clears the stack keeping tp-count while the timer runs; single-bhop-reset clears the bhop memory). Trigger set complete. |
| Mapping API | ✅ DONE | `MapApiSourceModule` reads entity keyvalues (native `CEntityKeyValues` via `CreateWorldInternal` detour) → parser → zones + all trigger types register. Multi-course track mapping remains as a future item (today all-track-0). |
| Courses | ✅ DONE | Course-switch on foreign start-zone touch (stops run, adopts course, cs2kz behavior) + named courses (timer_course_name). Record pipeline mode-keyed end to end (save/announce/PB/listings). |
| Global API | 🔴 EXTERNAL | NOT missing functionality — it's a *client* to KZGlobalteam's central server. Client shell built + dormant (`kz_global_apikey` empty = local ranking only). Real global submission needs their key AND their plugin-checksum approval; local records are 100% functional without it. |
| Racing / 1v1 | 🔴 EXTERNAL | Unbuilt; cs2kz's is a WebSocket cross-server coordinator gated on their backend. |
| Jumpstats | ✅ DONE | Distance/tiers (all 84 numbers exact) + DB persist + jump-type classify. sync/badAngles/overlap/deadAir/width bit-exact via native AACall telemetry through cs2kz Strafe::End. Gain-efficiency, external gain/loss, block/edge/landingEdge, failstat, miss (AlwaysFailstat pose-walk), jsAlways/jsFailstats — all ported + parity-fixed (LAJ +32, strafe count, width-avg, airtime cap). Minor: per-subtick resolution, ladder block variant. |
| Anticheat | ✅ DONE | 6 real detectors (bhop-hack window, snaptap, desubtick, autostrafe, strafe-optimizer, invalid-cvar) — all constants exact vs cs2kz, parity-hardened: per-slot state resets on connect/disconnect (no false-ban inheritance), cvar checks via async QueryConVar + sv_cheats grace, snaptap/desubtick kick-only. Replay-evidence clips attached to flags. Autoban pipeline (kz_ac_autoban). |
| Replays | ✅ DONE | Record + bot-driven playback work. Frame format is a subset (no duck/jump/subtick/weapon — documented). "Global" replay storage is a self-hosted blob store, non-interop with cs2kz.org by design. |
| Options / Preferences | ✅ DONE | DB-persisted per-player store, published cross-plugin (IKzPreferences Get/Set). Keys wired as features land (mode/fov/styles/hide/beam/paint/jsFailstats/jsAlways). cs2kz's full 65-key set grows with feature coverage. |
| Quiet (!hide) | ✅ DONE | Per-viewer transmit hiding of players + weapons, preference-persisted. Sounds suppressed too (PostEventAbstract on FireBullets/WeaponSound/SosStartSound, weapon-owner resolution, spectated-target exempt) — hidden players invisible AND inaudible, like cs2kz. |
| Beam (!beam) | ✅ DONE | Airborne env_beam trail, preference-persisted, teleport-artifact guard. Single beam style (cs2kz ships a particle addon we don't). |
| Paint (!paint) | ✅ DONE | Standalone plugin. **Real engine decals active** (UTIL_DecalTrace sig + tier0 _MakeGlobalSymbol, GE_PlaceDecalEvent recolor — cs2kz's exact mechanism) with env_beam fallback. Pending only a human eyeballing the rendered pixels. |
| Rank titles (!rank) | ✅ DONE | Standalone plugin. cs2kz's title ladder over the local points percentile; swaps to global percentiles if a key lands. |
| Ztopwatch (!zt) | ✅ DONE | Personal practice stopwatch between two placed zones (beam edges), start/jump/land toggles, main-timer + tick gates. Minor: per-point status readout. |
| Spec (!spec) | ✅ DONE | Spectate-by-name, slay-before-switch ghost-pawn guard, first-person observer lock. Minor: spectator-list HUD line. |
| Language / i18n | ✅ DONE | Mechanism complete via LocalizerManager (per-client Steam-language culture — better than cs2kz's !lang; en-US fallback). 24 keys carry cs2kz's real translations across 11 languages. Volume grows with features; a few plugin strings still English. |
| HUD | 🟡 PARTIAL | One HTML block, `h:mm:ss.mmm` formatted. Remaining: perf-color tint + compact mode + particle MHUD — cosmetic, would need a HUD redesign; low value. |
| Tip | 🟡 PARTIAL | Broadcast cycling with **configurable interval (kz_tip_interval convar) + Fisher-Yates shuffle** (no repeats within a cycle). Remaining: external tip-file loading (minor). |
| Ban | ✅ DONE | `!ban`/`!unban` with ACL + connect-time kick + automated AC→ban pipeline (kz_ac_autoban). Minor: RemoveBans hard-deletes vs soft-expire. |
| Goto / Fov / Measure / Pistol / Noclip | 🟡 PARTIAL | Goto (timer-block done), Fov, Pistol (**strip-enforce done** — replaces not stacks), Noclip (**respawn safeguard done**), Measure (**eye aim-trace done** — measures where you look). Measure `clear` subcommand added. Remaining: Pistol team-awareness (minor). |
| Database | ✅ DONE | SqlSugar dual-backend + LiteDB fallback (live), mode-keyed records. Minor: no CRC-versioned migration ledger (cs2kz has one). |
| Commands | ✅ DONE | Dict dispatch with real ACL. ~87 vs cs2kz ~132 names — the gap tracks the two 🟡 utility rows, not a framework limit. |
| Saveloc/loadloc | ⚪ N/A | cs2kz's own is an empty stub — not a Kreedz gap. |

## Proposed priority order to reach real 1:1

1. **Mapping API wiring + cs2kz zone/trigger engine** — unblocks real kz_ maps (zones→timer). Needs a live entity-keyvalue source (native `CEntityKeyValues` read on spawn) + porting the 14 trigger types + TriggerFix.
2. **Prestrafe metric fix** (wishdir-vs-velocity) — headline CKZ mechanic, small change.
3. **VNL real movement + both-modes CanTouchTimerZone / triggerfix** — core fidelity.
4. **Timer start-gate / pause / safeguard-pro** — real KZ timer semantics.
5. **Jumpstats per-mode/per-type tier tables + missing stats + Jumpbug**.
6. **AC → ban enforcement pipeline** (Infraction→Finalize).
7. **Global-API full client** (queries + ack handling) — validate against an issued key.
8. **Missing utilities**: quiet/hide, beam, paint, spec-by-name, rank-titles, ztopwatch.
9. **Real i18n** (phrase system + per-player language).
10. **Mode/style safety guards, checkpoint startpos persistence, degraded-util polish, DB migration ledger.**
11. **Racing** (last — gated on KZGlobalteam's coordinator backend).

## What genuinely works right now (live-verified on f1b935e4)

All modules load clean; SQL DB connected; VNL + 6 styles + jumpstats + anticheat + HUD register; CKZ managed
physics (prestrafe/perf/air-cap) + the 3 Core-owned native detours install and dispatch; `!` command layer,
ranks, manual ban, checkpoints, mode/fov/style prefs. Movement *feel* on legacy-named maps works; modern
mapping-API maps do not (see the ⚠️ above).
