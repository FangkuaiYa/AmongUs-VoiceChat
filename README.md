# Interstellar Voice Chat

Real-time proximity voice chat for Among Us. A single BepInEx plugin DLL plus a lightweight Go relay server.

## Project Structure

```
AmongUs-VoiceChat/
├── Interstellar.sln
├── voice-server-go/           # Go voice relay server
│   ├── main.go
│   ├── Dockerfile
│   └── internal/
│       ├── crypto/            #   AES-256-GCM encryption
│       ├── audio/             #   Jitter buffer, FEC, rate limiting
│       ├── protocol/          #   Binary message protocol
│       └── server/            #   WebSocket + HTTP + room manager
├── Interstellar.Client/       # BepInEx plugin — audio engine + game integration
│   ├── Network/               #   Binary protocol + AES-GCM crypto
│   ├── Routing/               #   Audio routing graph (mixer, filters, panner)
│   ├── VoiceChat/             #   Mic, Speaker, VCRoom
│   ├── Game/                  #   Among Us integration (HUD, config, settings UI)
│   ├── Patches/               #   Harmony patches
│   ├── Android/               #   Android mic/speaker via Starlight
│   └── AudioConstants.cs      #   Shared audio constants
├── docker-compose.yml         # Docker: Go Server + Coturn
├── nuget.config
├── turnserver.conf
└── .github/workflows/build.yml
```

## Build

**Prerequisites:** Go 1.26+ (server), .NET 6 SDK (plugin)

```bash
# ── Go Server ──
cd voice-server-go
go build -buildvcs=false -o voice-server .

# ── Plugin (two-pass: first compile, second embeds dependencies) ──
dotnet build Interstellar.Client/Interstellar.Client.csproj -c Release
dotnet build Interstellar.Client/Interstellar.Client.csproj -c Release
```

**Install:** Copy `Interstellar.Client.dll` into `BepInEx/plugins/`.

## Server

### Quick Start

```bash
# Basic (HTTP, port 8000)
./voice-server -addr :8000

# Production with TURN and optimizations
./voice-server \
  -addr 0.0.0.0:22021 \
  -optimal 100 \
  -turn turn:your-turn-server.com:3478 \
  -turn-user your-username \
  -turn-pass your-password \
  -redundancy 1

# With TLS (WSS)
./voice-server -addr :22021 -tls-cert cert.pem -tls-key key.pem

# Docker
docker compose up -d voice-server
```

### CLI Reference

```
voice-server [flags]

  -addr string               Listen address (default ":8000")
  -optimal int               Optimal player count (triggers capacity warning)
  -turn string               TURN server URL (e.g., turn:ip:3478)
  -turn-user string          TURN username
  -turn-pass string          TURN password
  -tls-cert string           TLS certificate path (enables WSS)
  -tls-key string            TLS key path (enables WSS)
  -secret string             AES-256-GCM key (64 hex chars = 32 bytes, optional)
  -redundancy int            Audio redundancy for loss mitigation (0=off, 1=2x, 2=3x)
  -max-bandwidth int         Max bandwidth per client in bytes/sec (0=unlimited)
```

### Environment Variables (Docker)

| Variable | Equivalent Flag | Default |
|----------|----------------|---------|
| `OPTIMAL_PLAYERS` | `-optimal` | `0` |
| `TURN_URL` | `-turn` | (empty) |
| `TURN_USER` | `-turn-user` | (empty) |
| `TURN_PASS` | `-turn-pass` | (empty) |
| `MAX_BANDWIDTH_PER_CLIENT` | `-max-bandwidth` | `0` |

### Dashboard

Visit `http://your-server:22021/`.

| Endpoint | Response |
|----------|----------|
| `GET /` | HTML dashboard (rooms, clients, encryption status, redundancy) |
| `GET /health` | `{"status":"ok"}` |
| `GET /stats` | `{"status":"ok","clients":5,"rooms":2,...}` |
| `GET /api/rooms` | Full room list with player details |

### Server Features

| Feature | Description |
|---------|-------------|
| **AES-256-GCM** | Optional application-layer encryption with per-frame random nonce. |
| **Audio Redundancy** | Sends each Opus frame N+1 times for packet loss mitigation. |
| **Jitter Buffer** | 5-frame (~100ms) reorder buffer per audio source. |
| **Bandwidth Limiter** | Token-bucket per-client rate limiting. |
| **Zero-decode Relay** | Server never decodes Opus — pure passthrough with minimal latency. |
| **Ping/Pong** | 15s WebSocket keep-alive, 45s timeout disconnect. |
| **Docker** | ~8 MB Alpine image. |

## Voice Server Matching

The plugin resolves each Among Us region to a voice server URL through three layers:

| Priority | Source | Behavior |
|----------|--------|----------|
| 1 | `ForceVoiceServer` | Overrides everything — all regions use a single VC server |
| 2 | `CustomServerListJson` | Overrides API entries with the same `name` |
| 3 | API | Fetched from the configured server list API at startup |
| Fallback | Built-in default | Used when no match is found |

**API and custom server format:**

```json
{
  "servers": [
    {
      "name": "Region Name",
      "address": "game-server.example.com",
      "port": 443,
      "vc": "ws://voice-server.example.com:22021",
      "vcLocation": "Location Label"
    }
  ]
}
```

- `name` — Among Us region name (case-insensitive)
- `vc` — WebSocket URL of the voice server
- `vcLocation` — Human-readable label shown in HUD

## Plugin Config

`BepInEx/config/com.voicechatplugin.cn.cfg`:

```ini
[VoiceChat]
MicrophoneDevice =
SpeakerDevice =
ServerAddress =             # Override VC server (blank = auto-match)
MasterVolume = 1.0
MicVolume = 1.0

[VoiceChat.Server]
UseApiServerList = true
CustomServerListJson =      # One-line JSON; overrides API entries
ForceVoiceServerEnabled = false
ForceVoiceServerUrl =       # VC WebSocket URL when force is enabled
EncryptionKey =             # AES-256-GCM key (64 hex chars, optional)

[VoiceChat.Room]
MaxChatDistance = 6
WallsBlockSound = true
OnlyHearInSight = false
ImpostorHearGhosts = false
OnlyGhostsCanTalk = false
HearInVent = true
VentPrivateChat = false
CommsSabDisables = true
CameraCanHear = true
ImpostorPrivateRadio = false
OnlyMeetingOrLobby = false
```

### Keyboard Shortcuts

| Key | Function |
|-----|----------|
| `F1` | Toggle VC settings window |
| `M` | Cycle mic mode: Global → Impostor Radio → Muted |
| `N` | Toggle speaker on/off |

## Docker

```bash
# Edit turnserver.conf with your TURN credentials, then:
docker compose up -d
```

| Port | Protocol | Service |
|------|----------|---------|
| 22021 | TCP | Go Voice Server WebSocket |
| 3478 | TCP+UDP | Coturn STUN/TURN |
| 5349 | TCP+UDP | Coturn TURN TLS |
| 49152–49252 | UDP | Coturn relay |

## CI

GitHub Actions builds on push:

- **Server** — Go cross-compile: `linux-amd64`, `linux-arm64`, `windows-amd64`, `darwin-amd64`
- **Client** — .NET 6 BepInEx plugin (two-pass build)
- **Docker** — Alpine image build (on tags)
- **Release** — Auto-create GitHub Release with all artifacts (on tags)

## Credits

- [NAudio](https://github.com/naudio/NAudio) — .NET audio library
- [Concentus](https://github.com/lostromb/concentus) — .NET Opus codec
- [Coturn](https://github.com/coturn/coturn) — TURN/STUN server
- [Gorilla WebSocket](https://github.com/gorilla/websocket) — Go WebSocket library

## License

MIT
