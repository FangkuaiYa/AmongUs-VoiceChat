# VoiceChatPlugin

A standalone voice chat BepInEx plugin for Among Us.  
By FangkuaiYa (and Dolly) Tester: Elinmei Farewell TAIKongguo Imp11  
Server by TAIKongguo  
Client Setting UI by farewell  
**Just place the single `VoiceChatPlugin.dll` file into `BepInEx/plugins/` and it works.**

---

## Why does it work with just one DLL?

BepInEx (IL2CPP) only automatically loads the **plugin DLL itself** placed in the `plugins/` folder.  
External dependencies such as `NAudio`, `SIPSorcery`, `Interstellar` are not automatically loaded.

This plugin solves this with the following approach:

1. **All dependency DLLs are embedded as EmbeddedResources inside `VoiceChatPlugin.dll`**  
   After building, the single `VoiceChatPlugin.dll` contains all the libraries.

2. **A hook is registered to `AppDomain.AssemblyResolve` in the `static` constructor**  
   Whenever the CLR requests an assembly, it loads it from the embedded resource and returns it.  
   The `static` constructor is guaranteed to execute before BepInEx's `Load()`, so the hook is active before any Interstellar types are used.

```csharp
// VoiceChatPluginMain.cs (excerpt)
static VoiceChatPluginMain()
{
    AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
}

private static Assembly? ResolveAssembly(object? sender, ResolveEventArgs args)
{
    var shortName = new AssemblyName(args.Name).Name;          // e.g., "NAudio.Core"
    var resourceName = "Lib." + shortName + ".dll";            // Embedded resource name
    using var stream = Assembly.GetExecutingAssembly()
                               .GetManifestResourceStream(resourceName);
    if (stream == null) return null;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Assembly.Load(ms.ToArray());
}
```

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

All DLLs inside the `Libs/` folder will be embedded into `VoiceChatPlugin.dll`.  
Place the generated `bin/Release/net6.0/VoiceChatPlugin.dll` into your `plugins/` folder.

> **Note:** With `<CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>`, no dependency DLLs other than `VoiceChatPlugin.dll` are **generated** in the build output folder.

---

## Configuration File

After the first launch, `BepInEx/config/VoiceChatPlugin.cfg` is generated.

| Key                | Default               | Description                                      |
|--------------------|----------------------|--------------------------------------------------|
| `MicrophoneDevice` | (empty = OS default) | Microphone device name to use                    |
| `SpeakerDevice`    | (empty = OS default) | Speaker device name to use (PC only)             |
| `ServerAddress`    | (empty = official server) | Server address in `ws://address:port` format   |
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
| Interstellar.dll + Interstellar.Messages.dll    | WebRTC voice communication |
| NAudio (Core / Wasapi / WinMM / Asio / Midi)    | Windows audio I/O        |
| SIPSorcery + SIPSorceryMedia.Abstractions       | WebRTC signaling         |
| BouncyCastle.Cryptography                        | DTLS / SRTP encryption   |
| Concentus                                       | Opus codec               |
| DnsClient                                       | ICE candidate resolution |
| Microsoft.Extensions.{DI,Logging}.Abstractions  | SIPSorcery dependencies  |
| System.Diagnostics.DiagnosticSource             | SIPSorcery dependencies  |
| websocket-sharp                                 | WebSocket signaling      |

---

## Project Structure

```
VoiceChatPlugin/
├── VoiceChatPlugin.csproj       ← Embeds all DLLs as EmbeddedResource
├── VoiceChatPluginMain.cs       ← AssemblyResolve hook + BepInEx entry point
├── Libs/                        ← Dependency DLLs storage (embedded at build time)
│   ├── Interstellar.dll
│   ├── Interstellar.Messages.dll
│   ├── NAudio*.dll
│   ├── SIPSorcery*.dll
│   └── ...
└── VoiceChat/
    ├── VoiceComponent.cs        ← IVoiceComponent interface
    ├── VoiceChatConfig.cs       ← BepInEx ConfigEntry based settings
    ├── VoiceChatRoom.cs         ← Room management & audio routing
    ├── VCPlayer.cs              ← Per-client volume control
    └── VoiceChatPatches.cs      ← Harmony patches
```