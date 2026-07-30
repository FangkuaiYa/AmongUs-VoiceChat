using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceChatConfig
{
    public static VoiceChatRoomSettings SyncedRoomSettings { get; } = new();

    /// <summary>Fired when synced room settings change (received from host via voice server).</summary>
    public static Action<VoiceChatRoomSettings>? OnSyncedSettingsChanged;

    public static string MicrophoneDevice => _mic?.Value ?? "";
    public static string SpeakerDevice => _speaker?.Value ?? "";
    public static string ServerAddress => _server?.Value ?? "";
    public static float MasterVolume => _masterVol?.Value ?? 1f;
    public static float MicVolume => _micVol?.Value ?? 1f;

    public static float HostMaxChatDistance => _hostMaxDist?.Value ?? 6f;
    public static bool HostWallsBlockSound => _hostWallsBlock?.Value ?? true;
    public static bool HostImpostorHearGhosts => _hostImpGhost?.Value ?? false;
    public static bool HostOnlyGhostsCanTalk => _hostOnlyGhost?.Value ?? false;
    public static bool HostHearInVent => _hostHearVent?.Value ?? true;
    public static bool HostHearVentPlayers => _hostHearVentPlayers?.Value ?? true;
    public static bool HostVentPrivateChat => _hostVentChat?.Value ?? false;
    public static bool HostCommsSabDisables => _hostCommSab?.Value ?? true;
    public static bool HostCameraCanHear => _hostCamera?.Value ?? true;
    public static bool HostImpostorPrivateRadio => _hostImpRadio?.Value ?? false;
    public static bool HostOnlyMeetingOrLobby => _hostMeetingOnly?.Value ?? false;

    /// <summary>Whether to fetch server list from the API.</summary>
    public static bool UseApiServerList => _useApiServerList?.Value ?? true;

    /// <summary>Custom server list in JSON format (same structure as the API).</summary>
    public static string CustomServerListJson => _customServerListJson?.Value ?? "";

    /// <summary>Force all Among Us servers to use a single voice server.</summary>
    public static bool ForceVoiceServerEnabled => _forceVoiceServerEnabled?.Value ?? false;

    /// <summary>Voice server URL to force when ForceVoiceServerEnabled is true.</summary>
    public static string ForceVoiceServerUrl => _forceVoiceServerUrl?.Value ?? "";

    /// <summary>AES-256-GCM encryption key (64 hex chars). Empty = no encryption.</summary>
    public static string EncryptionKey => _encryptionKey?.Value ?? "";

    private static ConfigEntry<string>? _mic, _speaker, _server;
    private static ConfigEntry<float>? _masterVol, _micVol;
    private static ConfigEntry<float>? _hostMaxDist;
    private static ConfigEntry<bool>? _hostWallsBlock, _hostImpGhost;
    private static ConfigEntry<bool>? _hostOnlyGhost, _hostHearVent, _hostHearVentPlayers, _hostVentChat;
    private static ConfigEntry<bool>? _hostCommSab, _hostCamera, _hostImpRadio, _hostMeetingOnly;
    private static ConfigEntry<bool>? _useApiServerList, _forceVoiceServerEnabled;
    private static ConfigEntry<string>? _customServerListJson, _forceVoiceServerUrl, _encryptionKey;

    /// <summary>Cached list of microphone device names for UI cycling.</summary>
    public static List<string> MicrophoneDevices { get; } = new();
    /// <summary>Cached list of speaker device names for UI cycling.</summary>
    public static List<string> SpeakerDevices { get; } = new();

    /// <summary>Whether device selection is supported on this platform.</summary>
    public static bool DeviceSelectionSupported =>
        Application.platform != RuntimePlatform.Android;

    private static bool _devicesCached;

    /// <summary>Refreshes the cached device lists. Safe to call on any platform — no-op on Android.
    /// Subsequent calls after the first successful enumeration are no-ops unless forced.</summary>
    public static void RefreshDeviceCaches(bool force = false)
    {
        if (!DeviceSelectionSupported) return;
        if (_devicesCached && !force) return;

        MicrophoneDevices.Clear();
        MicrophoneDevices.Add(""); // Default
        try
        {
            for (int i = 0; i < NAudio.Wave.WaveInEvent.DeviceCount; i++)
            {
                var c = NAudio.Wave.WaveInEvent.GetCapabilities(i);
                if (!string.IsNullOrWhiteSpace(c.ProductName))
                    MicrophoneDevices.Add(c.ProductName);
            }
        }
        catch { }

        SpeakerDevices.Clear();
        SpeakerDevices.Add(""); // Default
        try
        {
            using var e = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(
                NAudio.CoreAudioApi.DataFlow.Render,
                NAudio.CoreAudioApi.DeviceState.Active))
                if (!string.IsNullOrWhiteSpace(d.FriendlyName))
                    SpeakerDevices.Add(d.FriendlyName);
        }
        catch { }

        _devicesCached = true;
    }

    public static void Init(ConfigFile cfg)
    {
        _mic = cfg.Bind("VoiceChat", "MicrophoneDevice", "",
                        "Microphone device name. Leave empty for default.");
        _speaker = cfg.Bind("VoiceChat", "SpeakerDevice", "",
                        "Speaker device name. Leave empty for default.");
        _server = cfg.Bind("VoiceChat", "ServerAddress", "",
                        "VC server URL (e.g. ws://example.com:22021). Leave empty for official server.");
        _masterVol = cfg.Bind("VoiceChat", "MasterVolume", 1f,
                        new ConfigDescription("Master output volume", new AcceptableValueRange<float>(0.1f, 2f)));
        _micVol = cfg.Bind("VoiceChat", "MicVolume", 1f,
                        new ConfigDescription("Mic input volume", new AcceptableValueRange<float>(0.1f, 2f)));

        _hostMaxDist = cfg.Bind("VoiceChat.Room", "MaxChatDistance", 6f,
                            new ConfigDescription("Max hearing distance", new AcceptableValueRange<float>(1.5f, 20f)));
        _hostWallsBlock = cfg.Bind("VoiceChat.Room", "WallsBlockSound", true);
        _hostImpGhost = cfg.Bind("VoiceChat.Room", "ImpostorHearGhosts", false);
        _hostOnlyGhost = cfg.Bind("VoiceChat.Room", "OnlyGhostsCanTalk", false);
        _hostHearVent = cfg.Bind("VoiceChat.Room", "HearInVent", true);
        _hostHearVentPlayers = cfg.Bind("VoiceChat.Room", "HearVentPlayers", true);
        _hostVentChat = cfg.Bind("VoiceChat.Room", "VentPrivateChat", false);
        _hostCommSab = cfg.Bind("VoiceChat.Room", "CommsSabDisables", true);
        _hostCamera = cfg.Bind("VoiceChat.Room", "CameraCanHear", true);
        _hostImpRadio = cfg.Bind("VoiceChat.Room", "ImpostorPrivateRadio", false);
        _hostMeetingOnly = cfg.Bind("VoiceChat.Room", "OnlyMeetingOrLobby", false);

        // Server configuration
        _useApiServerList = cfg.Bind("VoiceChat.Server", "UseApiServerList", true,
            "Fetch available servers from the API. If disabled, only the custom server list is used.");
        _customServerListJson = cfg.Bind("VoiceChat.Server", "CustomServerListJson", "",
            "Custom server list in JSON format (same structure as the API: {\"servers\":[{...}]}). Merged with API results if API is enabled.");
        _forceVoiceServerEnabled = cfg.Bind("VoiceChat.Server", "ForceVoiceServerEnabled", false,
            "Force all Among Us servers to use a single voice server URL.");
        _forceVoiceServerUrl = cfg.Bind("VoiceChat.Server", "ForceVoiceServerUrl", "",
            "Voice server WebSocket URL to use when ForceVoiceServerEnabled is true. Leave empty for default fallback.");
        _encryptionKey = cfg.Bind("VoiceChat.Server", "EncryptionKey", "",
            "AES-256-GCM encryption key as 64 hex characters (32 bytes). Leave empty to disable encryption. Must match the server's -secret flag.");

        ApplyLocalHostSettingsToSynced();
    }

    public static void SetMicrophoneDevice(string v) => _mic!.Value = v;
    public static void SetSpeakerDevice(string v) => _speaker!.Value = v;
    public static void SetMasterVolume(float v) => _masterVol!.Value = v;
    public static void SetMicVolume(float v) => _micVol!.Value = v;
    public static void SetHostMaxChatDistance(float v) => _hostMaxDist!.Value = Math.Clamp(v, 1.5f, 20f);
    public static void SetHostWallsBlockSound(bool v) => _hostWallsBlock!.Value = v;
    public static void SetHostImpostorHearGhosts(bool v) => _hostImpGhost!.Value = v;
    public static void SetHostOnlyGhostsCanTalk(bool v) => _hostOnlyGhost!.Value = v;
    public static void SetHostHearInVent(bool v) => _hostHearVent!.Value = v;
    public static void SetHostHearVentPlayers(bool v) => _hostHearVentPlayers!.Value = v;
    public static void SetHostVentPrivateChat(bool v) => _hostVentChat!.Value = v;
    public static void SetHostCommsSabDisables(bool v) => _hostCommSab!.Value = v;
    public static void SetHostCameraCanHear(bool v) => _hostCamera!.Value = v;
    public static void SetHostImpostorPrivateRadio(bool v) => _hostImpRadio!.Value = v;
    public static void SetHostOnlyMeetingOrLobby(bool v) => _hostMeetingOnly!.Value = v;

    public static void ApplyLocalHostSettingsToSynced()
    {
        var s = SyncedRoomSettings;
        s.MaxChatDistance = HostMaxChatDistance;
        s.WallsBlockSound = HostWallsBlockSound;
        s.ImpostorHearGhosts = HostImpostorHearGhosts;
        s.OnlyGhostsCanTalk = HostOnlyGhostsCanTalk;
        s.HearInVent = HostHearInVent;
        s.HearVentPlayers = HostHearVentPlayers;
        s.VentPrivateChat = HostVentPrivateChat;
        s.CommsSabDisables = HostCommsSabDisables;
        s.CameraCanHear = HostCameraCanHear;
        s.ImpostorPrivateRadio = HostImpostorPrivateRadio;
        s.OnlyMeetingOrLobby = HostOnlyMeetingOrLobby;
    }
}
