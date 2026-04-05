# VoiceChatPlugin

A standalone voice chat BepInEx plugin for Among Us.  
By FangkuaiYa
Some Tester: Elinmei Farewell TAIKongguo Imp11  
Server Among Us (this Voice Chat function use Among Us Server)  
**Just place the single `VoiceChatPlugin.dll` file into `BepInEx/plugins/` and it works.**

---

## Installation

```
VoiceChatPlugin.dll  →  <Among Us>/BepInEx/plugins/
```

**That's it.** No other files are needed.

---

## Build Instructions

### Prerequisites

- .NET 6 SDK
- Network access to BepInEx / AmongUs NuGet packages

### Steps

```bash
cd VoiceChatPlugin
dotnet build -c Release
```

Place the generated `bin/Release/net6.0/VoiceChatPlugin.dll` into your `plugins/` folder.

> **Note:** With `<CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>`, no dependency DLLs other than `VoiceChatPlugin.dll` are **generated** in the build output folder.

---

## Configuration File

After the first launch, `BepInEx/config/VoiceChatPlugin.cfg` is generated.

| Key                | Default               | Description                                      |
|--------------------|----------------------|--------------------------------------------------|
| `MicrophoneDevice` | (empty = OS default) | Microphone device name to use                    |
| `SpeakerDevice`    | (empty = OS default) | Speaker device name to use (PC only)             |
| `MasterVolume`     | `1.0`                | Output volume [0.0 – 2.0]                        |
| `MicVolume`        | `1.0`                | Microphone input volume [0.0 – 2.0]              |

---

## In-Game Controls

| Key | Function              |
|-----|-----------------------|
| `M` | Mute ON / OFF         |

---

## Dependent Libraries (all embedded inside the DLL)

| Library                                         | Purpose                  |
|-------------------------------------------------|--------------------------|
| None | None |

---

## Project Structure

```
VoiceChatPlugin/
├── VoiceChatPlugin.csproj       ← Embeds all DLLs as EmbeddedResource
├── VoiceChatPluginMain.cs       ← AssemblyResolve hook + BepInEx entry point
├── ………………
└── VoiceChat/
    ├── ……………………
    ├── VoiceComponent.cs        ← IVoiceComponent interface
    ├── VoiceChatConfig.cs       ← BepInEx ConfigEntry based settings
    ├── VoiceChatRoom.cs         ← Room management & audio routing
    ├── VCPlayer.cs              ← Per-client volume control
    └── VoiceChatPatches.cs      ← Harmony patches
```
