# Voice Chat

一个为Among Us提供语音聊天功能的BepInEx插件。

## 安装

从https://github.com/FangkuaiYa/AmongUs-VoiceChat/releases下载与您的客户端Among Us版本对应的插件。
- 如果您下载的是zip文件，请注意Steam和Epic的字符，下载完成后将压缩包内的文件解压到游戏根目录，打开游戏即可。
- 如果您下载的是dll文件，您需要事先为您的Among Us安装BepInEx，然后将下载好的dll放到/BepInEx/plugins文件夹。
  注意：1.0.0以后的版本需要使用Reactor Among Us插件，您可以在此处下载对应的Reactor.dll：https://github.com/NuclearPowered/Reactor/releases

## 使用

使用非本地服务器创建房间后就可以享受语音聊天功能了！

## 构建说明

### 前置要求

- .NET 6 SDK
- 能够访问BepInEx / AmongUs NuGet包的网络

### 构建步骤

cd VoiceChatPlugin
dotnet build -c Release

将生成的bin/Release/net6.0/VoiceChatPlugin.dll放入您的plugins/文件夹。

> **注意:** 当 `<CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>`, 时，构建输出文件夹中不会生成除VoiceChatPlugin.dll以外的其他依赖DLL。  

## 游戏内控制

| 按键 | 功能              |
|-----|-----------------------|
| `M` | Mute ON / OFF         |


## 贡献者

- ThreeXThreeTeam(https://github.com/ThreeXThreeTeam) - 一个由TAIKongguo创建的开发团队，提供中国大陆的Among Us服务器等。
- Nebula on the Ship(https://github.com/Dolly1016/Nebula) - by Dolly，语音聊天功能的基础代码参考。
- NAudio(https://github.com/naudio/NAudio) - .NET的音频与MIDI库。
- Concentus(https://github.com/lostromb/concentus) - Opus音频编解码器的纯可移植C#/Java/Golang实现。
- BetterCrewLink(https://github.com/OhMyGuus/BetterCrewlink) - 一些设置选项和功能的灵感提供。

## 测试者

Elinmei，TAIKongguo，Farewell……
