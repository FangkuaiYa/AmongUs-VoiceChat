using Interstellar.Routing.Router;
using Interstellar.VoiceChat;
using System.Diagnostics.CodeAnalysis;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceChatRoom
{
	public static VoiceChatRoom? Current { get; private set; }

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
	public float LocalMicLevel => _localMicMeter?.Level ?? 0f;
	public bool Mute => _interstellar.Mute;
	public int SampleRate => _interstellar.SampleRate;

	private LevelMeterRouter.Property? _localMicMeter;

	// ── 破坏通讯检测缓存（避免每帧遍历）──────────────────────────────────
	private bool _commsSabActive;
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

	// ── Constructor ────────────────────────────────────────────────────
	private VoiceChatRoom(string region, string roomCode)
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
		SetMicrophone(VoiceChatConfig.MicrophoneDevice);

#if ANDROID
		// Android：延迟初始化扬声器，避免刚进房间时音频系统尚未就绪导致闪退
		_androidSpeakerSetupPending = true;
		_androidSpeakerSetupDelay = 1.0f;
#else
		SetSpeaker(VoiceChatConfig.SpeakerDevice);
#endif
		VoiceChatPluginMain.Logger.LogInfo("[VC] VoiceChatRoom constructed.");
	}

	// ── Device control ─────────────────────────────────────────────────
	public void SetMasterVolume(float v) => _masterVolumeProperty.Volume = v;
	public void SetMicVolume(float v) => _interstellar.Microphone?.SetVolume(v);
	public void SetLoopBack(bool lb) => _interstellar.SetLoopBack(lb);
	public void SetMute(bool mute) => _interstellar.SetMute(mute);
	public void ToggleMute() => SetMute(!Mute);

	public void SetMicrophone(string deviceName)
	{
		try
		{
#if ANDROID
			_interstellar.Microphone = new ManualMicrophone();
			StartAndroidMic(deviceName);
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
	// Android：用 ManualSpeaker 配合 Unity AudioSource 播放
	private UnityEngine.GameObject? _androidSpeakerGo;
	private UnityEngine.AudioSource? _androidAudioSource;
	private ManualSpeaker? _androidSpeaker;
	private bool _androidSpeakerSetupPending;
	private float _androidSpeakerSetupDelay;

    private void SetupAndroidSpeaker()
    {
        try
        {
            if (_androidSpeakerGo != null)
            {
                UnityEngine.Object.Destroy(_androidSpeakerGo);
                _androidSpeakerGo = null;
                _androidAudioSource = null;
                _androidSpeaker = null;
            }

            var go = new UnityEngine.GameObject("VC_AndroidSpeaker");
            _androidSpeakerGo = go;
            UnityEngine.Object.DontDestroyOnLoad(go);

            _androidAudioSource = go.AddComponent<UnityEngine.AudioSource>();
            if (_androidAudioSource == null)
            {
                VoiceChatPluginMain.Logger.LogError("[VC] Failed to create AudioSource for Android speaker.");
                return;
            }
            _androidAudioSource.spatialBlend = 0f;
            _androidAudioSource.playOnAwake = false;
            _androidAudioSource.Stop();

            _androidSpeaker = new ManualSpeaker(onClosed: null);
            if (_androidSpeaker == null)
            {
                VoiceChatPluginMain.Logger.LogError("[VC] Failed to create ManualSpeaker for Android.");
                UnityEngine.Object.Destroy(go);
                _androidSpeakerGo = null;
                _androidAudioSource = null;
                return;
            }

            int sr = _interstellar?.SampleRate ?? 48000;
            var puller = go.AddComponent<VCAndroidAudioPuller>();
            if (puller == null)
            {
                VoiceChatPluginMain.Logger.LogError("[VC] Failed to add VCAndroidAudioPuller component.");
                UnityEngine.Object.Destroy(go);
                _androidSpeakerGo = null;
                _androidAudioSource = null;
                _androidSpeaker = null;
                return;
            }

            puller.Init(_androidSpeaker, sr);

            var silentClip = UnityEngine.AudioClip.Create("VC_Silence", sr, 1, sr, false);
            _androidAudioSource.clip = silentClip;
            _androidAudioSource.loop = true;
            _androidAudioSource.volume = 0f;
            try
            {
                _androidAudioSource.Play();
            }
            catch (Exception playEx)
            {
                VoiceChatPluginMain.Logger.LogWarning($"[VC] Android AudioSource.Play() failed (may recover): {playEx.Message}");
            }

            if (_interstellar != null)
            {
                _interstellar.Speaker = _androidSpeaker;
                VoiceChatPluginMain.Logger.LogInfo("[VC] Android speaker setup completed (with defensive measures).");
            }
            else
            {
                VoiceChatPluginMain.Logger.LogError("[VC] Interstellar room is null, cannot assign speaker.");
            }
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Android speaker setup failed catastrophically: {ex.Message}");
            if (_androidSpeakerGo != null)
            {
                UnityEngine.Object.Destroy(_androidSpeakerGo);
                _androidSpeakerGo = null;
            }
            _androidAudioSource = null;
            _androidSpeaker = null;
        }
    }

    // Android mic 推送（每帧调用）
    private string _androidMicDevice = string.Empty;
	private UnityEngine.AudioClip? _androidMicClip;
	private int _androidMicLastPos;

	public void StartAndroidMic(string device = "")
	{
		if (!UnityEngine.Application.HasUserAuthorization(UnityEngine.UserAuthorization.Microphone))
		{
			VoiceChatPluginMain.Logger.LogWarning("[VC] Microphone permission not granted on Android, skipping mic capture.");
			_androidMicDevice = string.Empty;
			_androidMicClip = null;
			return;
		}

		_androidMicDevice = device ?? string.Empty;
		try
		{
			_androidMicClip = UnityEngine.Microphone.Start(_androidMicDevice, true, 1, 48000);
			_androidMicLastPos = 0;
		}
		catch (Exception ex)
		{
			VoiceChatPluginMain.Logger.LogError($"[VC] Android microphone start failed: {ex.Message}");
			_androidMicClip = null;
			_androidMicDevice = string.Empty;
		}
	}

	private void PushAndroidMicData()
	{
        if (_androidMicClip == null ||
        _interstellar?.Microphone is not ManualMicrophone mm)
        {
            return;
        }

		int cur = UnityEngine.Microphone.GetPosition(_androidMicDevice);
		if (cur == _androidMicLastPos) return;

		int count = cur > _androidMicLastPos
			? cur - _androidMicLastPos
			: _androidMicClip.samples - _androidMicLastPos + cur;

		var buf = new float[count];
		_androidMicClip.GetData(buf, _androidMicLastPos);
		_androidMicLastPos = cur;
		mm.PushAudioData(buf);
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

    // ── Per-frame Update ───────────────────────────────────────────────
    public void Update()
    {
        if (_interstellar == null) return;

        TryUpdateLocalProfile();

#if ANDROID
        if (_androidSpeakerSetupPending)
        {
            _androidSpeakerSetupDelay -= Time.deltaTime;
            if (_androidSpeakerSetupDelay <= 0f)
            {
                _androidSpeakerSetupPending = false;
                SetupAndroidSpeaker();
            }
        }

        if (_androidMicClip != null && _interstellar.Microphone is ManualMicrophone)
        {
            PushAndroidMicData();
        }
#endif

        // 破坏通讯状态（每 0.5 秒更新一次，减少遍历）
        _commsSabCheckTimer -= Time.deltaTime;
		if (_commsSabCheckTimer <= 0f)
		{
			_commsSabCheckTimer = 0.5f;
			_commsSabActive = CheckCommsSabotage();
		}

		var localPlayer = PlayerControl.LocalPlayer;
		Vector2? listenerPos = localPlayer ? (Vector2)localPlayer.transform.position : null;
		bool localInVent = localPlayer != null && localPlayer.inVent;

		// 虚拟扬声器（摄像头等）缓存
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

		bool inLobby = LobbyBehaviour.Instance != null;
		bool inMeeting = MeetingHud.Instance != null || ExileController.Instance != null;
		bool inGame = ShipStatus.Instance != null;

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

	// ── Lifecycle ──────────────────────────────────────────────────────
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
			if (_androidMicClip != null && UnityEngine.Microphone.IsRecording(_androidMicDevice))
			{
				UnityEngine.Microphone.End(_androidMicDevice);
			}
		}
		catch { }

		_androidMicClip = null;
		_androidMicDevice = string.Empty;
		_androidSpeakerSetupPending = false;

		if (_androidSpeakerGo != null)
		{
			UnityEngine.Object.Destroy(_androidSpeakerGo);
			_androidSpeakerGo = null;
		}

		_androidAudioSource = null;
		_androidSpeaker = null;
#endif

		_interstellar.Disconnect();
	}

	public bool TryGetPlayer(byte playerId, [MaybeNullWhen(false)] out VCPlayer player)
	{
		foreach (var c in _clients.Values)
			if (c.PlayerId == playerId) { player = c; return true; }
		player = null;
		return false;
	}

	// ── Profile ────────────────────────────────────────────────────────
	private byte _lastId = byte.MaxValue;
	private string _lastName = null!;

	private void TryUpdateLocalProfile() => UpdateLocalProfile(false);

	private void UpdateLocalProfile(bool always)
	{
		var lp = PlayerControl.LocalPlayer;
		if (!lp) return;
		if (always || lp.PlayerId != _lastId || lp.name != _lastName)
		{
			_lastId = lp.PlayerId;
			_lastName = lp.name;
			_interstellar.UpdateProfile(_lastName, _lastId);
		}
	}

	// ── Utilities ─────────────────────────────────────────────────────
	internal static float GetVolume(float dist, float maxDist)
		=> Math.Clamp(1f - dist / maxDist, 0f, 1f);

	internal static float GetPan(float micX, float spkX)
		=> Math.Clamp((spkX - micX) / 3f, -1f, 1f);

	internal record SpeakerCache(IVoiceComponent Speaker, float Volume, float Pan);
}
