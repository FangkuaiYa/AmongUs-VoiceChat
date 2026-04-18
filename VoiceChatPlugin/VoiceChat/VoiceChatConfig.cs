using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceChatConfig
{
    // Runtime-synced settings shared across all clients (pushed via Host RPC).
    public static VoiceChatRoomSettings SyncedRoomSettings { get; } = new();

    // Local audio device config
    public static string MicrophoneDevice => _mic?.Value  ?? "";
#if WINDOWS
    public static string SpeakerDevice    => _speaker?.Value ?? "";
#endif
    public static float  MasterVolume     => _masterVol?.Value ?? 1f;
    public static float  MicVolume        => _micVol?.Value    ?? 1f;

    // Per-player volume (keyed by byte playerId), stored in-memory only.
    // VoiceVolumeMenu reads/writes these to allow per-player volume sliders.
    private static readonly Dictionary<byte, float> _playerVolumes = new();

    public static float GetPlayerVolume(byte playerId)
        => _playerVolumes.TryGetValue(playerId, out var v) ? v : 1f;

    public static void SetPlayerVolume(byte playerId, float volume)
    {
        _playerVolumes[playerId] = Math.Clamp(volume, 0f, 2f);
        // Propagate to the live VCPlayer if a room is active
        if (VoiceChatRoom.Current != null && VoiceChatRoom.Current.TryGetPlayer(playerId, out var player))
        {
            player?.SetVolume(Math.Clamp(volume, 0f, 2f));
        }
    }

    // Host-only room settings (persisted locally, broadcast to all on game join).
    public static float HostMaxChatDistance      => _hostMaxDist?.Value      ?? 6f;
    public static bool  HostWallsBlockSound      => _hostWallsBlock?.Value   ?? true;
    public static bool  HostOnlyHearInSight      => _hostSight?.Value        ?? false;
    public static bool  HostImpostorHearGhosts   => _hostImpGhost?.Value     ?? false;
    public static bool  HostOnlyGhostsCanTalk    => _hostOnlyGhost?.Value    ?? false;
    public static bool  HostHearInVent           => _hostHearVent?.Value     ?? true;
    public static bool  HostVentPrivateChat      => _hostVentChat?.Value     ?? false;
    public static bool  HostCommsSabDisables     => _hostCommSab?.Value      ?? true;
    public static bool  HostCameraCanHear        => _hostCamera?.Value       ?? true;
    public static bool  HostImpostorPrivateRadio => _hostImpRadio?.Value     ?? false;
    public static bool  HostOnlyMeetingOrLobby   => _hostMeetingOnly?.Value  ?? false;

#if WINDOWS
    private static ConfigEntry<string>? _mic, _speaker;
#else
    private static ConfigEntry<string>? _mic;
#endif
    private static ConfigEntry<float>?  _masterVol, _micVol;
    private static ConfigEntry<float>?  _hostMaxDist;
    private static ConfigEntry<bool>?   _hostWallsBlock, _hostSight, _hostImpGhost;
    private static ConfigEntry<bool>?   _hostOnlyGhost, _hostHearVent, _hostVentChat;
    private static ConfigEntry<bool>?   _hostCommSab, _hostCamera, _hostImpRadio, _hostMeetingOnly;

    public static void Init(ConfigFile cfg)
    {
        _mic       = cfg.Bind("VoiceChat", "MicrophoneDevice", "",
                        "Microphone device name. Leave empty for default.");
#if WINDOWS
        _speaker   = cfg.Bind("VoiceChat", "SpeakerDevice", "",
                        "Speaker device name. Leave empty for default.");
#endif
        _masterVol = cfg.Bind("VoiceChat", "MasterVolume", 1f,
                        new ConfigDescription("Master output volume", new AcceptableValueRange<float>(0.1f, 2f)));
        _micVol    = cfg.Bind("VoiceChat", "MicVolume", 1f,
                        new ConfigDescription("Mic input volume",    new AcceptableValueRange<float>(0.1f, 2f)));

        _hostMaxDist     = cfg.Bind("VoiceChat.Room", "MaxChatDistance", 6f,
                            new ConfigDescription("Max hearing distance", new AcceptableValueRange<float>(1.5f, 20f)));
        _hostWallsBlock  = cfg.Bind("VoiceChat.Room", "WallsBlockSound",      true);
        _hostSight       = cfg.Bind("VoiceChat.Room", "OnlyHearInSight",       false);
        _hostImpGhost    = cfg.Bind("VoiceChat.Room", "ImpostorHearGhosts",    false);
        _hostOnlyGhost   = cfg.Bind("VoiceChat.Room", "OnlyGhostsCanTalk",     false);
        _hostHearVent    = cfg.Bind("VoiceChat.Room", "HearInVent",            true);
        _hostVentChat    = cfg.Bind("VoiceChat.Room", "VentPrivateChat",       false);
        _hostCommSab     = cfg.Bind("VoiceChat.Room", "CommsSabDisables",      true);
        _hostCamera      = cfg.Bind("VoiceChat.Room", "CameraCanHear",         true);
        _hostImpRadio    = cfg.Bind("VoiceChat.Room", "ImpostorPrivateRadio",  false);
        _hostMeetingOnly = cfg.Bind("VoiceChat.Room", "OnlyMeetingOrLobby",    false);

        ApplyLocalHostSettingsToSynced();
    }

    public static void SetMicrophoneDevice(string v)       => _mic!.Value = v;
#if WINDOWS
    public static void SetSpeakerDevice(string v)          => _speaker!.Value = v;
#endif
    public static void SetMasterVolume(float v)            => _masterVol!.Value = v;
    public static void SetMicVolume(float v)               => _micVol!.Value = v;
    public static void SetHostMaxChatDistance(float v)     => _hostMaxDist!.Value = Math.Clamp(v, 1.5f, 20f);
    public static void SetHostWallsBlockSound(bool v)      => _hostWallsBlock!.Value = v;
    public static void SetHostOnlyHearInSight(bool v)      => _hostSight!.Value = v;
    public static void SetHostImpostorHearGhosts(bool v)   => _hostImpGhost!.Value = v;
    public static void SetHostOnlyGhostsCanTalk(bool v)    => _hostOnlyGhost!.Value = v;
    public static void SetHostHearInVent(bool v)           => _hostHearVent!.Value = v;
    public static void SetHostVentPrivateChat(bool v)      => _hostVentChat!.Value = v;
    public static void SetHostCommsSabDisables(bool v)     => _hostCommSab!.Value = v;
    public static void SetHostCameraCanHear(bool v)        => _hostCamera!.Value = v;
    public static void SetHostImpostorPrivateRadio(bool v) => _hostImpRadio!.Value = v;
    public static void SetHostOnlyMeetingOrLobby(bool v)   => _hostMeetingOnly!.Value = v;

    /// <summary>Copies host-local config into SyncedRoomSettings before broadcasting.</summary>
    public static void ApplyLocalHostSettingsToSynced()
    {
        var s = SyncedRoomSettings;
        s.MaxChatDistance      = HostMaxChatDistance;
        s.WallsBlockSound      = HostWallsBlockSound;
        s.OnlyHearInSight      = HostOnlyHearInSight;
        s.ImpostorHearGhosts   = HostImpostorHearGhosts;
        s.OnlyGhostsCanTalk    = HostOnlyGhostsCanTalk;
        s.HearInVent           = HostHearInVent;
        s.VentPrivateChat      = HostVentPrivateChat;
        s.CommsSabDisables     = HostCommsSabDisables;
        s.CameraCanHear        = HostCameraCanHear;
        s.ImpostorPrivateRadio = HostImpostorPrivateRadio;
        s.OnlyMeetingOrLobby   = HostOnlyMeetingOrLobby;
    }
}
