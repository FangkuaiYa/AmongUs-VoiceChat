# Voice Chat

A BepInEx plugin that provides voice chat functionality for Among Us.

## Installation

Download the plugin corresponding to your Among Us client version from https://github.com/FangkuaiYa/AmongUs-VoiceChat/releases
- If you downloaded a zip file, please note the files for Steam and Epic versions. After downloading, extract all files from the archive into the game's root directory, then launch the game.
- If you downloaded a dll file, you need to have BepInEx installed for Among Us beforehand, then place the downloaded dll into the /BepInEx/plugins folder.
  Note: Versions 1.0.0 and later require the Reactor Among Us plugin. You can download the corresponding Reactor.dll from: https://github.com/NuclearPowered/Reactor/releases

## Usage

After creating a room using a non-local server, you can enjoy voice chat!

## Build Instructions

### Prerequisites

- .NET 6 SDK
- Network access to BepInEx / AmongUs NuGet packages

### Steps

cd VoiceChatPlugin
dotnet build -c Release

Place the generated bin/Release/net6.0/VoiceChatPlugin.dll into your plugins/ folder.

Note: With ''<CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>'', no dependency DLLs other than VoiceChatPlugin.dll are generated in the build output folder.

## In-Game Controls

Key: M
Function: Mute ON / OFF

## Contributors

- ThreeXThreeTeam (https://github.com/ThreeXThreeTeam) - A development team created by TAIKongguo, providing Among Us servers for mainland China, etc.
- Nebula on the Ship (https://github.com/Dolly1016/Nebula) - by Dolly, reference base code for voice chat functionality.
- NAudio (https://github.com/naudio/NAudio) - Audio and MIDI library for .NET.
- Concentus (https://github.com/lostromb/concentus) - Pure portable C#/Java/Golang implementations of the Opus audio codec.
- BetterCrewLink (https://github.com/OhMyGuus/BetterCrewlink) - Inspiration for some settings and features.

## Testers

Elinmei, TAIKongguo, Farewell……
