# BeanBot

Headless Among Us client, multi-bot lobby joiner, 15-bot self-hosted lobby orchestrator, and Windows desktop control GUI built on .NET 8 / C# 12 with direct Hazel UDP + Innersloth DTLS auth + EOS device authentication.

## Features
- **Multi-Bot Join & 15-Bot Self-Hosted Fleet**:
  - Join any existing lobby code (`4`- or `6`-letter code, or `auto`) with `1..15` bots, or self-host a `15`-bot Skeld lobby (`1` host + `14` joiners) with unique pre-assigned names and Appendix A colours (`Red` through `Banana`).
- **Desktop GUI (`BeanBot.Gui`) & Interactive CLI (`BeanBot.Cli`)**:
  - Dark-themed Windows Forms dashboard (`beanbot-gui.exe`) with live lobby/player tables, bot status, follow/task/vote/kill/vent controls, quick-chat & free-chat console, and live log viewer.
  - Full interactive menu and subcommand CLI (`beanbot.exe`) plus loopback HTTP control server (`127.0.0.1:8777`).
- **2-Step Live Innersloth Relay Auth & Transport**:
  - Live EOS device authentication (`api.epicgames.dev`) & Innersloth matchmaker (`/api/user`, `/api/games`) with automatic cross-region (`na`, `eu`, `as`) room code lookup and polite `TooManyRequests (116)` rate-limit handling.
  - Hazel DTLS 1.2 session-nonce auth on `relayPort + 2` with trust-on-first-use (TOFU) certificate pinning (`certs/<region>.der` + `.sha256`).
  - Plain Hazel UDP game relay on `relayPort` with `Hello(authNonce)`, selective ACKs, reliable retransmission, and fragmentation.
- **50 Hz Movement, Skeld Navigation & Autonomous Behaviors**:
  - `CustomNetworkTransform` 50 Hz fixed-step position batches (`0.02s` per sub-position) matching `GameAssembly.dll` remote interpolation.
  - Skeld polygon navmesh (`Skeld.nav`) with `AStar` + string-pulling funnel smoothing, nearest boundary-edge projection, sticky multi-step task completion (`2.5s` dwell), and real-time `follow` in both the Dropship lobby and The Skeld.
- **Autonomous Host & Supervision**:
  - `HostController`: owns Skeld `NormalGameOptions` (modular v12), starts private during seating and optionally flips public, kicks unexpected intruders while seating, holds 14 Ready -> 3s dwell before `StartGame`, and ends abandoned matches.
  - `Supervisor`: single-flight seat filler (`1 -> 4 -> 8 -> 14` ramp + jitter), 3-failure circuit breaker (`AccountState.Cooling`), per-bot crash isolation, and host-first reconnect (`< 60s`).

## Build & Test

```bash
# Build solution
dotnet build beanbot.sln

# Run offline unit & integration tests (includes multi-client 15-bot fleet & 50Hz follow harness)
dotnet test beanbot.sln --filter "Category!=Live"

# Run live integration smoke test against official Innersloth servers
BEANBOT_EOS_CLIENT_ID=<id> \
BEANBOT_EOS_CLIENT_SECRET=<secret> \
BEANBOT_EOS_DEPLOYMENT_ID=<deployment> \
BEANBOT_LIVE_TEST_ROOM=host \
BEANBOT_LIVE_REGION=na \
dotnet test beanbot.sln --filter "Category=Live" --logger "console;verbosity=detailed"
```

## Single-File Self-Contained Publish

### Windows x64 (CLI + Desktop GUI)
```powershell
dotnet publish src/BeanBot.Cli/BeanBot.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/win-x64
dotnet publish src/BeanBot.Gui/BeanBot.Gui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/win-x64
```

### Linux x64 (CLI)
```bash
dotnet publish src/BeanBot.Cli/BeanBot.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o dist/linux-x64
```

Only the resulting `beanbot.exe` / `beanbot-gui.exe` (or `beanbot` on Linux) binary is needed on the target machine. Vault files (`data/accounts.json.age`), session locks (`data/locks/`), replay logs (`data/replays/`), and TOFU pins (`certs/`) are created relative to the executable directory on first run.

## Vault Key & EOS Setup

- **Vault encryption key** (used to seal `data/accounts.json.age` with ChaCha20-Poly1305; prompted interactively if not set):
  ```bash
  export BEANBOT_VAULT_KEY="your-strong-vault-passphrase"
  ```
  Alternatively pass `--vault-key <passphrase>` on the CLI.

- **EOS Device Auth credentials** (required only for `--profile live` / authenticated private servers; prompted if not set):
  - `BEANBOT_EOS_CLIENT_ID`
  - `BEANBOT_EOS_CLIENT_SECRET`
  - `BEANBOT_EOS_DEPLOYMENT_ID`
  - Optional: `BEANBOT_EOS_EXTERNAL_AUTH_TYPE` (default: `deviceid_access_token`)

## Supported Live Regions

| Region Flag | Matchmaker Endpoint | Pin Files |
|---|---|---|
| `na` | `https://matchmaker.among.us` | `certs/na.der`, `certs/na.sha256` |
| `eu` | `https://matchmaker-eu.among.us` | `certs/eu.der`, `certs/eu.sha256` |
| `as` | `https://matchmaker-as.among.us` | `certs/as.der`, `certs/as.sha256` |

## Operator CLI & HTTP Control (`127.0.0.1:8777`)

```bash
# Launch interactive operator menu (when run with no arguments)
beanbot

# Self-host a 15-bot Skeld lobby on NA servers
beanbot host --bots 14 --map skeld --region na --public

# Join an existing lobby with multiple bots (or a single bot)
beanbot join <CODE|auto|host> --bots 5 --profile live --region na

# Verify protocol serialization & live client version
beanbot protocol-check

# Step through a G4 audit replay log
beanbot replay data/replays/<file>.jsonl [--all]

# Mark an account banned in the sealed vault
beanbot ban <accountId>
```

Loopback HTTP control endpoints (active during `host`, `join`, `serve`):
- `GET http://127.0.0.1:8777/status` — returns seats held (`/ 15`), host health, lifecycle state, counters, and per-bot snapshot.
- `POST http://127.0.0.1:8777/command` — body `{ "command": "follow all PlayerName" }` (supports `status`, `players`, `bots`, `stop [--all|<botId>]`, `say [botId|all] <phrase>`, `follow [botId|all] <player|off>`, `kill [botId] <player>`, `tasks [botId|all] on|off`, `vent [botId] <ventId>`, `vote [botId|all] <player|skip|clear>`, `ban <accountId>`).

## Certificate Rotation / Re-Pin Procedure

When Innersloth rotates a regional DTLS certificate, BeanBot aborts the handshake and logs both the pinned and presented SHA-256 hashes + subject. After confirming the rotation is expected, re-pin the region:

```bash
beanbot pin --accept-new na --endpoint <relay_ip>:<relay_port>
```

## Version Updates

All live protocol handshakes use the single `ProtocolVersion.CLIENT_VERSION` constant in `src/BeanBot.Protocol/Version.cs`. If a game update rejects the handshake (`IncorrectVersion` / `MismatchedVersion`):
1. Update `CLIENT_VERSION` and `CompatibilityGroups` in `src/BeanBot.Protocol/Version.cs`.
2. Rebuild and run `dotnet test beanbot.sln --filter "Category!=Live"` and `Category=Live`.
