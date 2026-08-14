using System.Diagnostics.CodeAnalysis;
using Interstellar.Routing;
using Interstellar.Routing.Router;
using Interstellar.Voice;
using UnityEngine;
using Interstellar.Android;

namespace Interstellar.Voice;

public class VoiceRoom
{
    public static VoiceRoom? Current { get; private set; }

    private readonly VCRoom _interstellar;
    private readonly VolumeRouter.Property _masterVolumeProperty;

    private readonly StereoRouter _imager;
    private readonly VolumeRouter _normalVolume, _ghostVolume, _radioVolume, _clientVolume;
    private readonly LevelMeterRouter _levelMeter;

    private readonly Dictionary<int, VCPlayer> _clients = new();
    public IEnumerable<VCPlayer> AllClients => _clients.Values;

    private readonly List<IVoiceComponent> _virtualMics = new();
    private readonly List<IVoiceComponent> _virtualSpeakers = new();
    public void AddVirtualMicrophone(IVoiceComponent c) => _virtualMics.Add(c);
    public void AddVirtualSpeaker(IVoiceComponent c) => _virtualSpeakers.Add(c);
    public void RemoveVirtualMicrophone(IVoiceComponent c) => _virtualMics.Remove(c);
    public void RemoveVirtualSpeaker(IVoiceComponent c) => _virtualSpeakers.Remove(c);

    public bool UsingMicrophone => _interstellar.Microphone != null;
    public float LocalMicLevel => _interstellar.LocalLevel;
    public bool IsClientImpostorRadio(int clientId) => _interstellar.IsClientImpostorRadio(clientId);
    public bool Mute => _interstellar.Mute;
    public bool HasSpeaker => _interstellar.Speaker != null;
    public int SampleRate => _interstellar.SampleRate;

    private LevelMeterRouter.Property? _localMicMeter;

    private int _androidNoRxFrames;
    private static bool _androidDidFullRestart;
    private bool _commsSabActive;
    private float _commsSabCheckTimer;

    private AndroidMicrophone? _androidMic;
    private AndroidSpeaker? _androidSpeaker;
    private static readonly bool IsAndroid = Application.platform == RuntimePlatform.Android;
    public static VoiceRoom Start(string region, string roomCode)
    {
        Current?.Close();
        Current = new VoiceRoom(region, roomCode);
        return Current;
    }

    public static void RestartForCurrentGame()
    {
        if (AmongUsClient.Instance == null) return;
        if (AmongUsClient.Instance.networkAddress is "127.0.0.1" or "localhost") return;
        Start(AmongUsClient.Instance.networkAddress, AmongUsClient.Instance.GameId.ToString());
    }

    public static void CloseCurrentRoom()
    {
        Current?.Close();
        Current = null;
        _androidDidFullRestart = false;
    }

    private VoiceRoom(string region, string roomCode)
    {
        SimpleRouter source = new();
        SimpleEndpoint endpoint = new();

        _imager = new StereoRouter();
        _normalVolume = new VolumeRouter();
        _ghostVolume = new VolumeRouter();
        _radioVolume = new VolumeRouter();
        _clientVolume = new VolumeRouter();
        _levelMeter = new LevelMeterRouter();

        FilterRouter ghostLowpass = FilterRouter.CreateLowPassFilter(1900f, 2f);
        ReverbRouter ghostReverb1 = new(53, 0.7f, 0.2f) { IsGlobalRouter = true };
        ReverbRouter ghostReverb2 = new(173, 0.4f, 0.6f) { IsGlobalRouter = true };
        FilterRouter radioHighpass = FilterRouter.CreateHighPassFilter(650f, 3.2f);
        FilterRouter radioLowpass = FilterRouter.CreateLowPassFilter(800f, 2.1f);
        DistortionFilter radioDistort = new() { IsGlobalRouter = true, DefaultThreshold = 0.55f };
        VolumeRouter masterRouter = new() { IsGlobalRouter = true };

        source.Connect(_clientVolume);
        _clientVolume.Connect(_imager);
        _imager.Connect(_levelMeter);
        _levelMeter.Connect(_normalVolume);
        _normalVolume.Connect(masterRouter);
        _imager.Connect(ghostLowpass);
        ghostLowpass.Connect(_ghostVolume);
        _ghostVolume.Connect(ghostReverb1);
        ghostReverb1.Connect(ghostReverb2);
        ghostReverb2.Connect(masterRouter);
        _clientVolume.Connect(radioHighpass);
        radioHighpass.Connect(radioLowpass);
        radioLowpass.Connect(_radioVolume);
        _radioVolume.Connect(radioDistort);
        radioDistort.Connect(masterRouter);
        masterRouter.Connect(endpoint);

        string server = VoiceConfig.GetActiveServerURL();

        _interstellar = new VCRoom(source, roomCode, region, server,
            new VCRoomParameters
            {
                OnConnectClient = (clientId, instance, isLocal) =>
                {
                    if (isLocal)
                    {
                        _clientVolume.GetProperty(instance).Volume = 1f;
                        _normalVolume.GetProperty(instance).Volume = 0f;
                        _localMicMeter = _levelMeter.GetProperty(instance);
                        // InterstellarPlugin.Logger.LogInfo("[VC] Local client connected.");
                    }
                    else
                    {
                        _clients[clientId] = new VCPlayer(this, instance,
                            _imager, _normalVolume, _ghostVolume, _radioVolume, _clientVolume, _levelMeter);
                        // InterstellarPlugin.Logger.LogInfo($"[VC] Remote client {clientId} connected.");
                    }
                },
                OnUpdateProfile = (clientId, playerId, playerName) =>
                {
                    if (_clients.TryGetValue(clientId, out var p))
                    {
                        p.UpdateProfile(playerId, playerName);
                        // InterstellarPlugin.Logger.LogInfo($"[VC] Client {clientId}: id={playerId} name={playerName}");
                    }
                },
                OnDisconnect = clientId =>
                {
                    _clients.Remove(clientId);
                    // InterstellarPlugin.Logger.LogInfo($"[VC] Client {clientId} disconnected.");
                },
            // Android jitter buffer: 240ms (11520 samples at 48kHz).
            // Smaller than the old 400ms to reduce latency and perceived echo,
            // but still large enough to absorb mobile network jitter.
            }.SetBufferLength(IsAndroid ? 11520 : 9600, IsAndroid ? 11520 : 2048));

        _masterVolumeProperty = masterRouter.GetProperty(_interstellar);
        SetMasterVolume(VoiceConfig.MasterVolume);
        _interstellar.SetLoopBack(false);

        if (IsAndroid)
        {
            SetupAndroidMicrophone();
            SetupAndroidSpeaker();
            // Kick off mic permission request + AudioRecord startup early
            // so the pipeline is warm by the time WebSocket/RTC connects.
            _androidMic?.Warmup();
            _androidSpeaker?.Warmup();
        }
        else
        {
            SetMicrophone(VoiceConfig.MicrophoneDevice);
            SetSpeaker(VoiceConfig.SpeakerDevice);
        }

        // InterstellarPlugin.Logger.LogInfo("[VC] VoiceRoom constructed (Interstellar transport).");
    }

    public void SetMasterVolume(float v) => _masterVolumeProperty.Volume = v;
    public void SetMicVolume(float v) => _interstellar.Microphone?.SetVolume(v);
    public void SetLoopBack(bool lb) => _interstellar.SetLoopBack(lb);
    public void SetMute(bool mute, bool isImpostorRadio = false) => _interstellar.SetMute(mute, isImpostorRadio);
    public void ToggleMute() => SetMute(!Mute);
    public void SendHostSettings(VoiceRoomSettings s) => _interstellar.SendHostSettings(s);

    public void SetMicrophone(string deviceName)
    {
        if (IsAndroid)
        {
            SetupAndroidMicrophone();
            return;
        }

        try
        {
            _interstellar.Microphone = new WindowsMicrophone(deviceName);
            _interstellar.Microphone?.SetVolume(VoiceConfig.MicVolume);
        }
        catch (Exception ex)
        {
            InterstellarPlugin.Logger.LogError($"[VC] Mic init failed: {ex.Message}");
            try { _interstellar.Microphone = null; } catch { }
        }
    }

    public void SetSpeaker(string deviceName)
    {
        if (IsAndroid)
        {
            SetupAndroidSpeaker();
            return;
        }

        try
        {
            _interstellar.Speaker = new WindowsSpeaker(deviceName);
        }
        catch (Exception ex)
        {
            InterstellarPlugin.Logger.LogError($"[VC] Speaker init failed: {ex.Message}");
            try { _interstellar.Speaker = null; } catch { }
        }
    }

    private void SetupAndroidMicrophone()
    {
        try
        {
            _androidMic?.Dispose();
            _androidMic = null;

            _androidMic = new AndroidMicrophone();
            _interstellar.Microphone = _androidMic.Microphone;
            _interstellar.Microphone?.SetVolume(VoiceConfig.MicVolume);
            InterstellarPlugin.Logger.LogInfo("[VC] Android mic (Starlight) initialised.");
        }
        catch (Exception ex)
        {
            InterstellarPlugin.Logger.LogError($"[VC] Android mic init failed: {ex.Message}");
            try { _androidMic?.Dispose(); } catch { }
            _androidMic = null;
        }
    }

    private void SetupAndroidSpeaker()
    {
        try
        {
            _androidSpeaker?.Dispose();
            _androidSpeaker = null;

            _androidSpeaker = new AndroidSpeaker();
            _androidSpeaker.Setup();
            _interstellar.Speaker = _androidSpeaker.Speaker; // Initialize BEFORE playback
            _androidSpeaker.StartPlayback(); // Start PCM callback AFTER Initialize
            InterstellarPlugin.Logger.LogInfo("[VC] Android speaker (AudioTrack) initialised.");
        }
        catch (Exception ex)
        {
            InterstellarPlugin.Logger.LogError($"[VC] Android speaker init failed: {ex.Message}");
            try { _androidSpeaker?.Dispose(); } catch { }
            _androidSpeaker = null;
        }
    }

    public void Update()
    {
        _androidMic?.Update();
        _androidSpeaker?.Update();

        TryUpdateLocalProfile();

        _commsSabCheckTimer -= Time.deltaTime;
        if (_commsSabCheckTimer <= 0f)
        {
            _commsSabCheckTimer = 0.5f;
            _commsSabActive = CheckCommsSabotage();
        }

        var localPlayer = PlayerControl.LocalPlayer;
        Vector2? listenerPos = localPlayer ? (Vector2)localPlayer.transform.position : null;
        bool localInVent = localPlayer != null && localPlayer.inVent;

        // Android first-join watchdog: if no incoming audio is received
        // within ~3s of the room going active, fully restart the connection.
        // Flag is static so it survives the restart and only fires once.
        if (IsAndroid && !_androidDidFullRestart && listenerPos.HasValue)
        {
            bool hasRx = false;
            foreach (var c in _clients.Values)
                if (c.Level > 0f) { hasRx = true; break; }
            if (hasRx)
            {
                _androidDidFullRestart = true;
            }
            else
            {
                _androidNoRxFrames++;
                if (_androidNoRxFrames >= 180)
                {
                    InterstellarPlugin.Logger.LogWarning(
                        "[VC] Android: no incoming audio after 3s — restarting room.");
                    _androidDidFullRestart = true;
                    Close();
                    RestartForCurrentGame();
                    return;
                }
            }
        }

        List<SpeakerCache> speakerCache = new();
        if (listenerPos.HasValue)
        {
            float maxRange = VoiceConfig.SyncedRoomSettings.MaxChatDistance;
            foreach (var v in _virtualSpeakers)
            {
                float d = Vector2.Distance(v.Position, listenerPos.Value);
                if (d < maxRange)
                    speakerCache.Add(new(v, GetVolume(d, maxRange), GetPan(listenerPos.Value.x, v.Position.x)));
            }
        }

        bool inLobby = LobbyBehaviour.Instance != null;
        bool inMeeting = MeetingHud.Instance != null || ExileController.Instance != null;
        bool inGame = ShipStatus.Instance != null;

        // Copy to avoid collection-modified-during-enumeration from audio callback thread
        var clients = _clients.Values.ToArray();
        foreach (var client in clients)
        {
            if (inLobby || !inGame)
                client.UpdateLobby();
            else if (inMeeting)
                client.UpdateMeeting();
            else
                client.UpdateTaskPhase(listenerPos, speakerCache, _virtualMics, localInVent, _commsSabActive);
        }
    }

    private static bool CheckCommsSabotage()
    {
        if (ShipStatus.Instance == null) return false;
        foreach (var sys in ShipStatus.Instance.Systems.Values)
        {
            var hud = sys.TryCast<HudOverrideSystemType>();
            if (hud != null && hud.IsActive) return true;
        }
        return false;
    }

    public void Rejoin()
    {
        _interstellar.Rejoin();
        UpdateLocalProfile(true);
        foreach (var c in _clients.Values) c.ResetMapping();
        _commsSabActive = false;
        // InterstellarPlugin.Logger.LogInfo("[VC] Rejoin: state cleared, profiles will re-sync.");
    }

    public void Close()
    {
        _androidMic?.Dispose();
        _androidMic = null;

        _androidSpeaker?.Dispose();
        _androidSpeaker = null;

        _interstellar.Disconnect();
    }

    public bool TryGetPlayer(byte playerId, [MaybeNullWhen(false)] out VCPlayer player)
    {
        foreach (var c in _clients.Values)
            if (c.PlayerId == playerId) { player = c; return true; }
        player = null;
        return false;
    }

    private byte _lastId = byte.MaxValue;
    private string _lastName = null!;

    private void TryUpdateLocalProfile() => UpdateLocalProfile(false);

    internal void ForceUpdateLocalProfile() => UpdateLocalProfile(true);

    public async System.Threading.Tasks.Task JoinPublicLobby(int lobbyId)
    {
        await _interstellar.JoinLobby(lobbyId, (err, code, server) =>
        {
            PublicLobbyManager.OnLobbyJoinResult?.Invoke(err, code, server);
        });
    }

    public async System.Threading.Tasks.Task WatchLobbyBrowserAsync(bool watch)
    {
        await _interstellar.WatchLobbyBrowser(watch);
    }

    public async System.Threading.Tasks.Task PublishLobbyAsync(string code, PublicLobbyManager.LobbyInfo info)
    {
        await _interstellar.PublishLobby(code, info);
    }

    public async System.Threading.Tasks.Task RemoveLobbyAsync(string code)
    {
        await _interstellar.RemoveLobby(code);
    }

    private void UpdateLocalProfile(bool always)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        if (!always && lp.PlayerId == _lastId && lp.name == _lastName) return;

        _lastId = lp.PlayerId;
        _lastName = lp.name;
        var cid = AmongUsClient.Instance ? AmongUsClient.Instance.ClientId : 0;
        _interstellar.UpdateProfile(_lastName, _lastId, cid);
    }

    internal static float GetVolume(float dist, float maxDist)
        => Math.Clamp(1f - dist / maxDist, 0f, 1f);

    internal static float GetPan(float micX, float spkX)
        => Math.Clamp((spkX - micX) / 3f, -1f, 1f);

    internal record SpeakerCache(IVoiceComponent Speaker, float Volume, float Pan);
}
