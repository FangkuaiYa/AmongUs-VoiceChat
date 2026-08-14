using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace Interstellar.Voice;

/// <summary>
/// BCL-compatible voice chat configuration.
/// Settings structure mirrors the BetterCrewLink independent client.
/// </summary>
public static class VoiceConfig
{
    public static VoiceRoomSettings SyncedRoomSettings { get; } = new();

    /// <summary>Fired when synced room settings change (received from host via voice server).</summary>
    public static Action<VoiceRoomSettings>? OnSyncedSettingsChanged;

    // ── Server ────────────────────────────────────────────
    public static int SelectedServerIndex
    {
        get => _serverIndex?.Value ?? 0;
        set { if (_serverIndex != null) _serverIndex.Value = value; }
    }
    public static string CustomServerURL
    {
        get => _customUrl?.Value ?? "";
        set { if (_customUrl != null) _customUrl.Value = value; }
    }
    public static string GetActiveServerURL()
    {
        var servers = ServerList.GetServers();
        if (SelectedServerIndex >= 0 && SelectedServerIndex < servers.Count)
            return servers[SelectedServerIndex].URL;
        return CustomServerURL;
    }
    public static bool IsBeijingServer => GetActiveServerURL().Contains("bcl.server.amongusclub.cn");

    public static string GetServerLocationName(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "Unknown";
        if (url.Contains("bcl-na.server.amongusclub.cn")) return "NA (US)";
        if (url.Contains("bcl.server.amongusclub.cn")) return "Beijing";
        if (url.Contains("bettercrewl.ink")) return "Official";
        return url.Length > 25 ? url[..25] + "..." : url;
    }

    // ── Audio devices ──────────────────────────────────────
    public static string MicrophoneDevice => _mic?.Value ?? "";
    public static string SpeakerDevice => _speaker?.Value ?? "";

    // ── Volume ─────────────────────────────────────────────
    public static float MasterVolume
    {
        get => Math.Clamp(_masterVol?.Value ?? 1f, 0.1f, 2f);
        set { if (_masterVol != null) _masterVol.Value = value; }
    }
    public static float MicVolume
    {
        get => Math.Clamp(_micVol?.Value ?? 1f, 0.1f, 2f);
        set { if (_micVol != null) _micVol.Value = value; }
    }

    // ── Audio processing ───────────────────────────────────
    public static bool NoiseSuppression
    {
        get => _noiseSuppression?.Value ?? true;
        set { if (_noiseSuppression != null) _noiseSuppression.Value = value; }
    }
    public static bool EchoCancellation
    {
        get => _echoCancellation?.Value ?? true;
        set { if (_echoCancellation != null) _echoCancellation.Value = value; }
    }

    // ── VAD ────────────────────────────────────────────────
    public static bool VADEnabled
    {
        get => _vadEnabled?.Value ?? true;
        set { if (_vadEnabled != null) _vadEnabled.Value = value; }
    }

    // ── Host room settings (synced via server when host) ───
    public static float HostMaxChatDistance
    {
        get => Math.Clamp(_hostMaxDist?.Value ?? 6f, 1.5f, 20f);
        set { if (_hostMaxDist != null) _hostMaxDist.Value = value; }
    }
    public static bool HostWallsBlockSound
    {
        get => _hostWallsBlock?.Value ?? true;
        set { if (_hostWallsBlock != null) _hostWallsBlock.Value = value; }
    }
    public static bool HostOnlyHearInSight
    {
        get => _hostOnlyHearInSight?.Value ?? false;
        set { if (_hostOnlyHearInSight != null) _hostOnlyHearInSight.Value = value; }
    }
    public static bool HostImpostorHearGhosts
    {
        get => _hostImpGhost?.Value ?? false;
        set { if (_hostImpGhost != null) _hostImpGhost.Value = value; }
    }
    public static bool HostOnlyGhostsCanTalk
    {
        get => _hostOnlyGhost?.Value ?? false;
        set { if (_hostOnlyGhost != null) _hostOnlyGhost.Value = value; }
    }
    public static bool HostHearInVent
    {
        get => _hostHearVent?.Value ?? true;
        set { if (_hostHearVent != null) _hostHearVent.Value = value; }
    }
    public static bool HostHearVentPlayers
    {
        get => _hostHearVentPlayers?.Value ?? true;
        set { if (_hostHearVentPlayers != null) _hostHearVentPlayers.Value = value; }
    }
    public static bool HostVentPrivateChat
    {
        get => _hostVentChat?.Value ?? false;
        set { if (_hostVentChat != null) _hostVentChat.Value = value; }
    }
    public static bool HostCommsSabDisables
    {
        get => _hostCommSab?.Value ?? true;
        set { if (_hostCommSab != null) _hostCommSab.Value = value; }
    }
    public static bool HostCameraCanHear
    {
        get => _hostCamera?.Value ?? true;
        set { if (_hostCamera != null) _hostCamera.Value = value; }
    }
    public static bool HostImpostorPrivateRadio
    {
        get => _hostImpRadio?.Value ?? false;
        set { if (_hostImpRadio != null) _hostImpRadio.Value = value; }
    }
    public static bool HostOnlyMeetingOrLobby
    {
        get => _hostMeetingOnly?.Value ?? false;
        set { if (_hostMeetingOnly != null) _hostMeetingOnly.Value = value; }
    }

    // ── Public lobby ───────────────────────────────────────
    public static bool PublicLobbyEnabled
    {
        get => _publicLobby?.Value ?? false;
        set { if (_publicLobby != null) _publicLobby.Value = value; }
    }
    public static string PublicLobbyTitle
    {
        get => _publicTitle?.Value ?? "";
        set { if (_publicTitle != null) _publicTitle.Value = value; }
    }
    public static string PublicLobbyLanguage
    {
        get => _publicLang?.Value ?? "en";
        set { if (_publicLang != null) _publicLang.Value = value; }
    }

    // ── Device caches ──────────────────────────────────────
    public static List<string> MicrophoneDevices { get; } = new();
    public static List<string> SpeakerDevices { get; } = new();
    public static bool DeviceSelectionSupported =>
        Application.platform != RuntimePlatform.Android;

    private static ConfigEntry<int>? _serverIndex;
    private static ConfigEntry<string>? _customUrl;
    private static ConfigEntry<string>? _mic, _speaker;
    private static ConfigEntry<float>? _masterVol, _micVol;
    private static ConfigEntry<bool>? _noiseSuppression, _echoCancellation;
    private static ConfigEntry<bool>? _vadEnabled;
    private static ConfigEntry<float>? _hostMaxDist;
    private static ConfigEntry<bool>? _hostWallsBlock, _hostOnlyHearInSight, _hostImpGhost;
    private static ConfigEntry<bool>? _hostOnlyGhost, _hostHearVent, _hostHearVentPlayers, _hostVentChat;
    private static ConfigEntry<bool>? _hostCommSab, _hostCamera, _hostImpRadio, _hostMeetingOnly;
    private static ConfigEntry<bool>? _publicLobby;
    private static ConfigEntry<string>? _publicTitle, _publicLang;

    private static bool _devicesCached;

    public static void RefreshDeviceCaches(bool force = false)
    {
        if (!DeviceSelectionSupported) return;
        if (_devicesCached && !force) return;

        MicrophoneDevices.Clear();
        MicrophoneDevices.Add("");
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
        SpeakerDevices.Add("");
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
        var serverNames = ServerList.GetServerNames();

        _serverIndex = cfg.Bind("VoiceChat.Server", "ServerIndex", 0,
            new ConfigDescription("Selected server index", new AcceptableValueRange<int>(0, serverNames.Length - 1)));
        _customUrl = cfg.Bind("VoiceChat.Server", "CustomURL", "",
            "Custom BCL server URL (used when last server option is selected).");

        _mic = cfg.Bind("VoiceChat", "MicrophoneDevice", "", "Microphone device name.");
        _speaker = cfg.Bind("VoiceChat", "SpeakerDevice", "", "Speaker device name.");
        _masterVol = cfg.Bind("VoiceChat", "MasterVolume", 1f,
            new ConfigDescription("Master output volume", new AcceptableValueRange<float>(0.1f, 2f)));
        _micVol = cfg.Bind("VoiceChat", "MicVolume", 1f,
            new ConfigDescription("Mic input volume", new AcceptableValueRange<float>(0.1f, 2f)));

        _noiseSuppression = cfg.Bind("VoiceChat", "NoiseSuppression", true);
        _echoCancellation = cfg.Bind("VoiceChat", "EchoCancellation", true);
        _vadEnabled = cfg.Bind("VoiceChat", "VADEnabled", true);

        _hostMaxDist = cfg.Bind("VoiceChat.Room", "MaxChatDistance", 6f,
            new ConfigDescription("Max hearing distance", new AcceptableValueRange<float>(1.5f, 20f)));
        _hostWallsBlock = cfg.Bind("VoiceChat.Room", "WallsBlockSound", true);
        _hostOnlyHearInSight = cfg.Bind("VoiceChat.Room", "OnlyHearInSight", false);
        _hostImpGhost = cfg.Bind("VoiceChat.Room", "ImpostorHearGhosts", false);
        _hostOnlyGhost = cfg.Bind("VoiceChat.Room", "OnlyGhostsCanTalk", false);
        _hostHearVent = cfg.Bind("VoiceChat.Room", "HearInVent", true);
        _hostHearVentPlayers = cfg.Bind("VoiceChat.Room", "HearVentPlayers", true);
        _hostVentChat = cfg.Bind("VoiceChat.Room", "VentPrivateChat", false);
        _hostCommSab = cfg.Bind("VoiceChat.Room", "CommsSabDisables", true);
        _hostCamera = cfg.Bind("VoiceChat.Room", "CameraCanHear", true);
        _hostImpRadio = cfg.Bind("VoiceChat.Room", "ImpostorPrivateRadio", false);
        _hostMeetingOnly = cfg.Bind("VoiceChat.Room", "OnlyMeetingOrLobby", false);

        _publicLobby = cfg.Bind("VoiceChat.Room", "PublicLobby", false);
        _publicTitle = cfg.Bind("VoiceChat.Room", "PublicTitle", "Among Us Lobby");
        _publicLang = cfg.Bind("VoiceChat.Room", "PublicLanguage", "en");

        ApplyLocalHostSettingsToSynced();
    }

    public static void SetMicrophoneDevice(string v) { if (_mic != null) _mic.Value = v; }
    public static void SetSpeakerDevice(string v) { if (_speaker != null) _speaker.Value = v; }
    public static void SetMasterVolume(float v) => MasterVolume = v;
    public static void SetMicVolume(float v) => MicVolume = v;

    public static void SetHostMaxChatDistance(float v) => HostMaxChatDistance = v;
    public static void SetHostWallsBlockSound(bool v) => HostWallsBlockSound = v;
    public static void SetHostOnlyHearInSight(bool v) => HostOnlyHearInSight = v;
    public static void SetHostImpostorHearGhosts(bool v) => HostImpostorHearGhosts = v;
    public static void SetHostOnlyGhostsCanTalk(bool v) => HostOnlyGhostsCanTalk = v;
    public static void SetHostHearInVent(bool v) => HostHearInVent = v;
    public static void SetHostHearVentPlayers(bool v) => HostHearVentPlayers = v;
    public static void SetHostVentPrivateChat(bool v) => HostVentPrivateChat = v;
    public static void SetHostCommsSabDisables(bool v) => HostCommsSabDisables = v;
    public static void SetHostCameraCanHear(bool v) => HostCameraCanHear = v;
    public static void SetHostImpostorPrivateRadio(bool v) => HostImpostorPrivateRadio = v;
    public static void SetHostOnlyMeetingOrLobby(bool v) => HostOnlyMeetingOrLobby = v;

    public static void ApplyLocalHostSettingsToSynced()
    {
        var s = SyncedRoomSettings;
        s.MaxChatDistance = HostMaxChatDistance;
        s.WallsBlockSound = HostWallsBlockSound;
        s.OnlyHearInSight = HostOnlyHearInSight;
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

/// <summary>
/// BCL server list — mirrors the server options available in BetterCrewLink.
/// </summary>
public static class ServerList
{
    public static IReadOnlyList<(string Name, string URL)> GetServers()
    {
        return new[]
        {
            ("BetterCrewLink Official", "https://bettercrewl.ink"),
            ("China,Beijing (AmongUsClub)", "https://bcl.server.amongusclub.cn"),
            ("North America (AmongUsClub)", "https://bcl-na.server.amongusclub.cn"),
        };
    }

    public static string[] GetServerNames()
    {
        var servers = GetServers();
        var names = new string[servers.Count + 1]; // +1 for Custom
        for (int i = 0; i < servers.Count; i++)
            names[i] = servers[i].Name;
        names[^1] = "Custom...";
        return names;
    }
}
