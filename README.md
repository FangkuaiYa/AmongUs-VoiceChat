# Interstellar Voice Chat

Real-time proximity voice chat for Among Us. A single BepInEx plugin DLL that connects to BetterCrewLink-compatible voice servers.

## Project Structure

```
AmongUs-VoiceChat/
├── Interstellar.sln
├── Interstellar.Client/       # BepInEx plugin — audio engine + game integration
│   ├── Network/               #   BCL-compatible Socket.IO protocol
│   ├── Routing/               #   Audio routing graph (mixer, filters, panner)
│   ├── Voice/                 #   Mic, Speaker, VCRoom, HUD buttons
│   ├── Game/                  #   Among Us integration (HUD, config, settings UI)
│   ├── Patches/               #   Harmony patches
│   ├── Android/               #   Android mic/speaker via Starlight
│   ├── NAudio/                #   Audio providers & effects
│   ├── Mixing/                #   Audio mixing
│   └── Resources/             #   Embedded sprites & locale strings
├── nuget.config
└── .github/workflows/build.yml
```

## Build

**Prerequisites:** .NET 6 SDK

```bash
dotnet build Interstellar.Client/Interstellar.Client.csproj -c Release
```

NAudio and Concentus are resolved from NuGet and embedded into the plugin DLL automatically, so a single build pass produces a self-contained plugin.

**Install:** Copy `Interstellar.Client.dll` into `BepInEx/plugins/`.

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

## CI

GitHub Actions builds on push:

- **Client** — .NET 6 BepInEx plugin (single-pass build, dependencies embedded)
- **Release** — Auto-create GitHub Release with the plugin (on tags)

## Credits

- [NAudio](https://github.com/naudio/NAudio) — .NET audio library
- [Concentus](https://github.com/lostromb/concentus) — .NET Opus codec
- [BetterCrewLink](https://github.com/OhMyGuus/BetterCrewLink) — voice server protocol reference

## License

MIT
