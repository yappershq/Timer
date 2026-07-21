<div align="center">
  <h1><strong>Kreedz</strong></h1>
  <p>A Kreedz (KZ) climbing gamemode for Counter-Strike 2 on ModSharp — a from-scratch reimplementation of cs2kz-metamod.</p>
</div>

<p align="center">
  <a href="https://github.com/Kxnrl/modsharp-public"><img src="https://img.shields.io/badge/framework-ModSharp-5865F2?logo=github" alt="ModSharp"></a>
  <img src="https://img.shields.io/badge/game-CS2-orange" alt="CS2">
  <img src="https://img.shields.io/github/license/yappershq/Kreedz" alt="License">
  <img src="https://img.shields.io/github/actions/workflow/status/yappershq/Kreedz/build.yml?branch=master" alt="Build">
  <img src="https://img.shields.io/github/stars/yappershq/Kreedz?style=flat&logo=github" alt="Stars">
</p>

---

> [!WARNING]
> Work in progress — subject to change and not yet production-ready. The bit-exact CKZ movement and the
> cross-plugin loading still need live-server validation (see [docs/STATUS.md](docs/STATUS.md)).

**Kreedz** is a full KZ gamemode: a strict run timer with PRO/STANDARD semantics, checkpoints &
teleports, movement **modes** (Vanilla + Classic), stackable **styles**, jumpstats, a live speed/keys HUD,
a dual-backend database with ban-excluded leaderboards, per-player preference persistence, an anticheat,
and an optional cs2kz global-API client. It's built on a fork of [Source2Surf/Timer](https://github.com/Source2Surf/Timer)
and reimplements the feature set of [KZGlobalteam/cs2kz-metamod](https://github.com/KZGlobalteam/cs2kz-metamod).

Modes and styles ship as **separate plugins** (like cs2kz), registering against the core — see
[docs/EXTENSIBILITY.md](docs/EXTENSIBILITY.md).

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `sharp/modules/Kreedz.Core/` | `<sharp>/modules/Kreedz.Core/` |
| `sharp/modules/Kreedz.RequestManager/` | `<sharp>/modules/Kreedz.RequestManager/` |
| `sharp/modules/Kreedz.Mode.VNL/` | `<sharp>/modules/Kreedz.Mode.VNL/` |
| `sharp/modules/Kreedz.Mode.CKZ/` | `<sharp>/modules/Kreedz.Mode.CKZ/` |
| `sharp/shared/Kreedz.Shared/` | `<sharp>/shared/Kreedz.Shared/` |
| `.assets/gamedata/kreedz.games.jsonc` | `<sharp>/gamedata/kreedz.games.jsonc` |
| `.assets/configs/*.jsonc` | `<sharp>/configs/` |

Restart the server (or change map) to load. `Kreedz.Mode.VNL` / `Kreedz.Mode.CKZ` are optional — install
the modes you want; the core runs without them (stock movement).

## 🧩 Dependencies

Uses the **ModSharp first-party modules** (ship with ModSharp): **AdminManager** (gates the ban
commands), **ConVar/Command** systems (commands + replicated mode/style convars).

`Kreedz.RequestManager` is the storage backend and talks to the core over the `IRequestManager`
cross-plugin interface — with no external DB configured it falls back to a bundled **LiteDB** store.

Bundled (ship inside the modules): `SqlSugar` (SQL ORM), `LiteDB` (offline fallback), `MemoryPack` +
`ZstdSharp` (replay serialization/compression).

## ⌨️ Commands

**Checkpoints & practice**

| Command | Aliases | Description |
|---------|---------|-------------|
| `!cp` | `!checkpoint` | Save a checkpoint at your position |
| `!tp` | `!teleport` | Teleport to your last checkpoint |
| `!undo` | | Undo the last checkpoint |
| `!prevcp` / `!nextcp` | `!pcp` / `!ncp` | Cycle through saved checkpoints |
| `!setstartpos` / `!clearstartpos` | `!ssp` / `!csp` | Set / clear a custom start position |

**Modes & styles**

| Command | Aliases | Description |
|---------|---------|-------------|
| `!mode` | `!vnl`, `!ckz` | Switch movement mode (Vanilla / Classic) |
| `!style` | `!togglestyle` | Toggle a style on/off |
| `!addstyle` / `!removestyle` | `!abh`, `!lgj` | Add / remove a style (Auto-Bhop, Legacy-Jump) |
| `!clearstyles` | | Remove all active styles |

**Movement utilities**

| Command | Description |
|---------|-------------|
| `!goto <player>` | Teleport to a player |
| `!fov <40-150>` | Set your field of view |
| `!measure` | Two-point distance tool |
| `!pistol <name>` | Give yourself a pistol |
| `!tips` | Toggle rotating tips |

**Records & ranks**

| Command | Description |
|---------|-------------|
| `!wr` / `!pb` | World record / your personal best |
| `!top` / `!rank` | Map leaderboard / your rank |
| `!recent` | Your recent runs |
| `!profile` / `!stats` | Your profile |
| `!swr` / `!spb` | Stage world record / personal best |
| `!btop` / `!bwr` / `!bpb` | Bonus leaderboards |
| `!mapinfo` / `!tier` | Map info / tier |
| `!global` | Global-API connection status |

**Admin**

| Command | Permission | Description |
|---------|-----------|-------------|
| `!ban <name\|steamid64> <minutes\|0=perm> [reason]` | `@kz/ban` | Ban a player from ranking (kicks on connect) |
| `!unban <steamid64>` | `@kz/ban` | Remove a player's bans |
| `!zone` | admin | In-game zone editor |
| `!set_tier <n>` | admin | Set the current map's tier |

## ⚙️ Configuration

Config files live in `<sharp>/configs/` (`.assets/configs/` in the repo):

| File | Controls |
|------|----------|
| `kreedz.jsonc` | Database connection (`database`) + remote replay storage (`replay`) |
| `kreedz-styles.jsonc` | Style definitions |
| `kreedz-replay.jsonc` | Replay-bot rules |

Key ConVars:

| ConVar | Default | Meaning |
|--------|---------|---------|
| `kz_global_apikey` | `""` | cs2kz global API key. **Empty = global disabled** (local ranking only) |
| `kz_global_url` | `https://api.cs2kz.org` | Global API base URL |
| `kz_ac_autokick` | `false` | Anticheat kicks flagged players instead of warning |

(Plus `timer_*` ConVars inherited from the base for replay/timer tuning.)

## 🔧 How it works

A run is timed from the start zone to the end zone; **0 teleports = PRO, ≥1 = STANDARD**. Movement
**modes** replicate a per-player convar layer (and, for Classic, custom prestrafe/perf physics); **styles**
stack on top and mark a run unranked. Records persist through `IRequestManager` (SQL or LiteDB fallback),
and leaderboards exclude banned players. See [docs/KZ_PORT_PLAN.md](docs/KZ_PORT_PLAN.md) for the
architecture and [docs/STATUS.md](docs/STATUS.md) for per-subsystem status.

## 🧩 Public API

`Kreedz.Shared` exposes the extension points. A mode/style plugin registers against the core in
`OnAllModulesLoaded`:

```csharp
var modes = sharpModuleManager
    .GetOptionalSharpModuleInterface<IKzModeRegistry>(IKzModeRegistry.Identity)?.Instance;
modes?.RegisterMode("mymode", "My Mode", "MYM", convars);
```

It also publishes `IKzStyleRegistry`, `IRequestManager`, and `IReplayProvider` for consumers.

## 📦 Build

```bash
dotnet build Kreedz.slnx -c Release
```

Outputs each project's `.dll` under its `bin/Release/net10.0/`; CI publishes the deployable
`modules/` + `shared/` layout.

## 🙏 Credits

- Built on [Source2Surf/Timer](https://github.com/Source2Surf/Timer) by **Nukoooo** & **Kxnrl** — the surf
  timer chassis this gamemode forks (zones, run timer, replays, DB, HUD). Licensed AGPL-3.0.
- Reimplements the feature set of [KZGlobalteam/cs2kz-metamod](https://github.com/KZGlobalteam/cs2kz-metamod)
  — the original CS2 KZ gamemode and the reference for modes, movement, jumpstats, and the global API.

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
