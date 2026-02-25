using Interstellar.Routing.Router;
using Interstellar.VoiceChat;
using NAudio.Wave;
using System.Diagnostics.CodeAnalysis;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceChatRoom
{
    public static VoiceChatRoom? Current { get; private set; }

    private readonly VCRoom              _interstellar;
    private readonly VolumeRouter.Property _masterVolumeProperty;

    private readonly StereoRouter     _imager;
    private readonly VolumeRouter     _normalVolume, _ghostVolume, _radioVolume, _clientVolume;
    private readonly LevelMeterRouter _levelMeter;

    private readonly Dictionary<int, VCPlayer> _clients = new();
    public IEnumerable<VCPlayer> AllClients => _clients.Values;

    private readonly List<IVoiceComponent> _virtualMics     = new();
    private readonly List<IVoiceComponent> _virtualSpeakers = new();
    public void AddVirtualMicrophone(IVoiceComponent c)    => _virtualMics.Add(c);
    public void AddVirtualSpeaker(IVoiceComponent c)       => _virtualSpeakers.Add(c);
    public void RemoveVirtualMicrophone(IVoiceComponent c) => _virtualMics.Remove(c);
    public void RemoveVirtualSpeaker(IVoiceComponent c)    => _virtualSpeakers.Remove(c);

    public bool  UsingMicrophone => _interstellar.Microphone != null;
    public float LocalMicLevel   => _localMicMeter?.Level ?? 0f;
    public bool  Mute            => _interstellar.Mute;
    public int   SampleRate      => _interstellar.SampleRate;

    private LevelMeterRouter.Property? _localMicMeter;

    // 破坏通讯状态缓存（每 0.5 秒检测一次）
    private bool  _commsSabActive;
    private float _commsSabCheckTimer;

    // ── Factory ────────────────────────────────────────────────────────
    public static VoiceChatRoom Start(string region, string roomCode)
    {
        Current?.Close();
        Current = new VoiceChatRoom(region, roomCode);
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
    }

    // ── 构造函数 ───────────────────────────────────────────────────────
    private VoiceChatRoom(string region, string roomCode)
    {
        SimpleRouter   source   = new();
        SimpleEndpoint endpoint = new();

        _imager       = new StereoRouter();
        _normalVolume = new VolumeRouter();
        _ghostVolume  = new VolumeRouter();
        _radioVolume  = new VolumeRouter();
        _clientVolume = new VolumeRouter();
        _levelMeter   = new LevelMeterRouter();

        FilterRouter    ghostLowpass  = FilterRouter.CreateLowPassFilter(1900f, 2f);
        ReverbRouter    ghostReverb1  = new(53,  0.7f, 0.2f) { IsGlobalRouter = true };
        ReverbRouter    ghostReverb2  = new(173, 0.4f, 0.6f) { IsGlobalRouter = true };
        FilterRouter    radioHighpass = FilterRouter.CreateHighPassFilter(650f, 3.2f);
        FilterRouter    radioLowpass  = FilterRouter.CreateLowPassFilter(800f, 2.1f);
        DistortionFilter radioDistort = new() { IsGlobalRouter = true, DefaultThreshold = 0.55f };
        VolumeRouter    masterRouter  = new() { IsGlobalRouter = true };

        source.Connect(_clientVolume);
        _clientVolume.Connect(_imager);
        _imager.Connect(_normalVolume);
        _normalVolume.Connect(_levelMeter);
        _levelMeter.Connect(masterRouter);
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

        string server = VoiceChatConfig.ServerAddress;
        if (string.IsNullOrEmpty(server)) server = "ws://118.25.84.234:22021";

        _interstellar = new VCRoom(source, roomCode, region, server + "/vc",
            new VCRoomParameters
            {
                OnConnectClient = (clientId, instance, isLocal) =>
                {
                    if (isLocal)
                    {
                        _clientVolume.GetProperty(instance).Volume = 1f;
                        _normalVolume.GetProperty(instance).Volume = 1f;
                        _localMicMeter = _levelMeter.GetProperty(instance);
                        VoiceChatPluginMain.Logger.LogInfo("[VC] Local client connected.");
                    }
                    else
                    {
                        _clients[clientId] = new VCPlayer(this, instance,
                            _imager, _normalVolume, _ghostVolume, _radioVolume, _clientVolume, _levelMeter);
                        VoiceChatPluginMain.Logger.LogInfo($"[VC] Remote client {clientId} connected.");
                    }
                },
                OnUpdateProfile = (clientId, playerId, playerName) =>
                {
                    if (_clients.TryGetValue(clientId, out var p))
                    {
                        p.UpdateProfile(playerId, playerName);
                        VoiceChatPluginMain.Logger.LogInfo($"[VC] Client {clientId}: id={playerId} name={playerName}");
                    }
                },
                OnDisconnect = clientId =>
                {
                    _clients.Remove(clientId);
                    VoiceChatPluginMain.Logger.LogInfo($"[VC] Client {clientId} disconnected.");
                },
            }.SetBufferLength(2048));

        _masterVolumeProperty = masterRouter.GetProperty(_interstellar);
        SetMasterVolume(VoiceChatConfig.MasterVolume);

        // ── 麦克风初始化 ────────────────────────────────────────────
        SetMicrophone(VoiceChatConfig.MicrophoneDevice);

// WaveFormat 由 Interstellar 内部 RTC 协商后自动注入，无需手动设置。

        // ── 扬声器初始化 ────────────────────────────────────────────
#if ANDROID
        // Android：立即创建 ManualSpeaker 并赋给 _interstellar，
        // 避免 Interstellar 内部在解码音频时因 Speaker==null 而崩溃。
        // VCAndroidAudioPuller 负责将 ManualSpeaker 的 PCM 输出到 Unity AudioSource。
        SetupAndroidSpeaker();
#else
        SetSpeaker(VoiceChatConfig.SpeakerDevice);
#endif

        VoiceChatPluginMain.Logger.LogInfo("[VC] VoiceChatRoom constructed.");
    }

    // ── 设备控制 ────────────────────────────────────────────────────────
    public void SetMasterVolume(float v) => _masterVolumeProperty.Volume = v;
    public void SetMicVolume(float v)    => _interstellar.Microphone?.SetVolume(v);
    public void SetLoopBack(bool lb)     => _interstellar.SetLoopBack(lb);
    public void SetMute(bool mute)       => _interstellar.SetMute(mute);
    public void ToggleMute()             => SetMute(!Mute);

    public void SetMicrophone(string deviceName)
    {
        try
        {
#if ANDROID
            // Android：使用 ManualMicrophone，由 Unity Microphone API + PushAudioData 驱动
            _interstellar.Microphone = new ManualMicrophone();
            // 仅在有权限时才启动麦克风录制
            if (Application.HasUserAuthorization(UserAuthorization.Microphone))
                StartAndroidMic(deviceName);
            else
                VoiceChatPluginMain.Logger.LogWarning("[VC] Mic permission not granted, mic capture skipped.");
#else
            _interstellar.Microphone = new WindowsMicrophone(deviceName);
#endif
            _interstellar.Microphone?.SetVolume(VoiceChatConfig.MicVolume);
            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC] Mic set: '{(string.IsNullOrEmpty(deviceName) ? "default" : deviceName)}'");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Mic init failed: {ex.Message}");
            try { _interstellar.Microphone = null; } catch { }
        }
    }

#if ANDROID
    // ── Android 专用：Speaker ─────────────────────────────────────────
    private GameObject?    _androidSpeakerGo;
    private ManualSpeaker? _androidSpeaker;

    private void SetupAndroidSpeaker()
    {
        try
        {
            // 清理旧实例
            if (_androidSpeakerGo != null)
            {
                Object.Destroy(_androidSpeakerGo);
                _androidSpeakerGo = null;
            }

            // ① 先创建 ManualSpeaker 并赋给 _interstellar.Speaker
            //   这一步必须在 GameObject 创建之前完成，
            //   确保 Interstellar 解码线程写入数据时 Speaker 不为 null。
            _androidSpeaker = new ManualSpeaker(onClosed: null);

            // WaveFormat 由 Interstellar 内部 RTC 协商后自动注入，无需手动设置。
            // ManualSpeaker 没有公开的 WaveFormat setter，格式通过 ISpeakerContext 注入。
            _interstellar.Speaker = _androidSpeaker;
            VoiceChatPluginMain.Logger.LogInfo("[VC] ManualSpeaker assigned to Interstellar.");

            // ② 创建承载 AudioSource 和 VCAndroidAudioPuller 的 GameObject
            _androidSpeakerGo = new GameObject("VC_AndroidSpeaker");
            Object.DontDestroyOnLoad(_androidSpeakerGo);

            var audioSource = _androidSpeakerGo.AddComponent<AudioSource>();
            audioSource.playOnAwake  = false;
            audioSource.spatialBlend = 0f;

            // ③ 添加拉取组件（已通过 ClassInjector 注册，可安全 AddComponent）
            var puller = _androidSpeakerGo.AddComponent<VCAndroidAudioPuller>();
            puller.Init(_androidSpeaker);

            VoiceChatPluginMain.Logger.LogInfo("[VC] Android speaker setup complete.");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Android speaker setup failed: {ex.Message}\n{ex.StackTrace}");
            // 即便 puller 失败，Speaker 已经赋值，Interstellar 不会因 null 崩溃
            // 只是没有声音输出，不会导致玩家卡退
        }
    }

    // ── Android 专用：Mic ────────────────────────────────────────────
    private string     _androidMicDevice = string.Empty;
    private AudioClip? _androidMicClip;
    private int        _androidMicLastPos;

    private void StartAndroidMic(string device = "")
    {
        try
        {
            _androidMicDevice = device ?? string.Empty;
            _androidMicClip   = Microphone.Start(_androidMicDevice, true, 1, 48000);
            _androidMicLastPos = 0;
            VoiceChatPluginMain.Logger.LogInfo($"[VC] Android mic started: '{_androidMicDevice}'");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Android mic start failed: {ex.Message}");
            _androidMicClip   = null;
            _androidMicDevice = string.Empty;
        }
    }

    private void PushAndroidMicData()
    {
        if (_androidMicClip == null) return;
        if (_interstellar.Microphone is not ManualMicrophone mm) return;

        int cur = Microphone.GetPosition(_androidMicDevice);
        if (cur == _androidMicLastPos) return;

        // count 是帧数（与声道无关的 per-channel sample 数）
        int frames = cur > _androidMicLastPos
            ? cur - _androidMicLastPos
            : _androidMicClip.samples - _androidMicLastPos + cur;
        if (frames <= 0) return;

        int ch = _androidMicClip.channels;
        var raw = new float[frames * ch];
        _androidMicClip.GetData(raw, _androidMicLastPos);
        _androidMicLastPos = cur;

        // PushAudioData 内部期望单声道 48000Hz PCM。
        // Unity Microphone 在 Android 某些设备上即使指定 1ch 也可能返回 2ch，
        // 因此这里统一做下混：若是立体声则取左右声道平均值。
        float[] mono;
        if (ch == 1)
        {
            mono = raw;
        }
        else
        {
            mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < ch; c++) sum += raw[i * ch + c];
                mono[i] = sum / ch;
            }
        }
        mm.PushAudioData(mono);
    }
#else
    public void SetSpeaker(string deviceName)
    {
        try
        {
            _interstellar.Speaker = new WindowsSpeaker(deviceName);
            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC] Speaker set: '{(string.IsNullOrEmpty(deviceName) ? "default" : deviceName)}'");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Speaker init failed: {ex.Message}");
            try { _interstellar.Speaker = null; } catch { }
        }
    }
#endif

    // ── 每帧 Update ────────────────────────────────────────────────────
    public void Update()
    {
        if (_interstellar == null) return;

        TryUpdateLocalProfile();

#if ANDROID
        PushAndroidMicData();
#endif

        // 破坏通讯状态（每 0.5 秒检测一次）
        _commsSabCheckTimer -= Time.deltaTime;
        if (_commsSabCheckTimer <= 0f)
        {
            _commsSabCheckTimer = 0.5f;
            _commsSabActive     = CheckCommsSabotage();
        }

        var localPlayer  = PlayerControl.LocalPlayer;
        Vector2? listenerPos = localPlayer ? (Vector2)localPlayer.transform.position : null;
        bool localInVent = localPlayer != null && localPlayer.inVent;

        // 虚拟扬声器缓存
        List<SpeakerCache> speakerCache = new();
        if (listenerPos.HasValue)
        {
            float maxRange = VoiceChatConfig.SyncedRoomSettings.MaxChatDistance;
            foreach (var v in _virtualSpeakers)
            {
                float d = Vector2.Distance(v.Position, listenerPos.Value);
                if (d < maxRange)
                    speakerCache.Add(new(v, GetVolume(d, maxRange), GetPan(listenerPos.Value.x, v.Position.x)));
            }
        }

        bool inLobby   = LobbyBehaviour.Instance != null;
        bool inMeeting = MeetingHud.Instance != null || ExileController.Instance != null;
        bool inGame    = ShipStatus.Instance != null;

        foreach (var client in _clients.Values)
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

    // ── 生命周期 ────────────────────────────────────────────────────────
    public void Rejoin()
    {
        _interstellar.Rejoin();
        UpdateLocalProfile(true);
        foreach (var c in _clients.Values) c.ResetMapping();
    }

    public void Close()
    {
#if ANDROID
        try
        {
            if (_androidMicClip != null && Microphone.IsRecording(_androidMicDevice))
                Microphone.End(_androidMicDevice);
        }
        catch { }

        _androidMicClip   = null;
        _androidMicDevice = string.Empty;

        if (_androidSpeakerGo != null)
        {
            Object.Destroy(_androidSpeakerGo);
            _androidSpeakerGo = null;
        }
        _androidSpeaker = null;
#endif
        try { _interstellar.Disconnect(); } catch { }
    }

    public bool TryGetPlayer(byte playerId, [MaybeNullWhen(false)] out VCPlayer player)
    {
        foreach (var c in _clients.Values)
            if (c.PlayerId == playerId) { player = c; return true; }
        player = null;
        return false;
    }

    // ── 本地 Profile ────────────────────────────────────────────────────
    private byte   _lastId   = byte.MaxValue;
    private string _lastName = null!;

    private void TryUpdateLocalProfile() => UpdateLocalProfile(false);

    private void UpdateLocalProfile(bool always)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        if (always || lp.PlayerId != _lastId || lp.name != _lastName)
        {
            _lastId   = lp.PlayerId;
            _lastName = lp.name;
            _interstellar.UpdateProfile(_lastName, _lastId);
        }
    }

    // ── 工具方法 ────────────────────────────────────────────────────────
    internal static float GetVolume(float dist, float maxDist)
        => Math.Clamp(1f - dist / maxDist, 0f, 1f);

    internal static float GetPan(float micX, float spkX)
        => Math.Clamp((spkX - micX) / 3f, -1f, 1f);

    internal record SpeakerCache(IVoiceComponent Speaker, float Volume, float Pan);
}
