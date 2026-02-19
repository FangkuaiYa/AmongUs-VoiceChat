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
	public float LocalMicLevel => 0f; // TODO: expose via LevelMeterRouter.GlobalLevel if available
	public bool Mute => _interstellar.Mute;
	public int SampleRate => _interstellar.SampleRate;

	public static VoiceChatRoom Start(string region, string roomCode)
	{
		Current?.Close();
		var room = new VoiceChatRoom(region, roomCode);
		Current = room;
		return room;
	}

	public static void RestartForCurrentGame()
	{
		if (AmongUsClient.Instance == null) return;
		if (AmongUsClient.Instance.networkAddress is "127.0.0.1" or "localhost") return;

		string region = AmongUsClient.Instance.networkAddress;
		string roomId = AmongUsClient.Instance.GameId.ToString();
		VoiceChatPluginMain.Logger.LogInfo($"[VC] Restart voice chat – region={region} room={roomId}");
		Start(region, roomId);
	}

	public static void CloseCurrentRoom()
	{
		Current?.Close();
		Current = null;
	}

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
		if (string.IsNullOrEmpty(server)) server = "ws://vchk.amongusclub.cn:22021";

		_interstellar = new VCRoom(source, roomCode, region, server + "/vc",
			new VCRoomParameters
			{
				OnConnectClient = (clientId, instance, isLocal) =>
				{
					if (isLocal)
					{
						_clientVolume.GetProperty(instance).Volume = 1f;
						_normalVolume.GetProperty(instance).Volume = 1f;
						VoiceChatPluginMain.Logger.LogInfo($"[VC] Local client connected, instance {instance.GetHashCode()}");
					}
					else
					{
						_clients[clientId] = new VCPlayer(this, instance,
							_imager, _normalVolume, _ghostVolume, _radioVolume,
							_clientVolume, _levelMeter);
						VoiceChatPluginMain.Logger.LogInfo($"[VC] Remote client {clientId} connected.");
					}
				},
				OnUpdateProfile = (clientId, playerId, playerName) =>
				{
					if (_clients.TryGetValue(clientId, out var p))
					{
						p.UpdateProfile(playerId, playerName);
						VoiceChatPluginMain.Logger.LogInfo($"[VC] Client {clientId} updated profile: playerId={playerId}, name={playerName}");
					}
				},
				OnDisconnect = (clientId) =>
				{
					_clients.Remove(clientId);
					VoiceChatPluginMain.Logger.LogInfo($"[VC] Client {clientId} disconnected.");
				},
			}.SetBufferLength(2048));

		_masterVolumeProperty = masterRouter.GetProperty(_interstellar);

		SetMasterVolume(VoiceChatConfig.MasterVolume);
		SetMicrophone(VoiceChatConfig.MicrophoneDevice);
#if !ANDROID
		SetSpeaker(VoiceChatConfig.SpeakerDevice);
#endif

		VoiceChatPluginMain.Logger.LogInfo("[VC] VoiceChatRoom constructed and routing graph built.");
	}

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
#else
			_interstellar.Microphone = new WindowsMicrophone(deviceName);
#endif
			string displayName = string.IsNullOrEmpty(deviceName) ? "(default)" : deviceName;
			if (_interstellar.Microphone == null)
				VoiceChatPluginMain.Logger.LogError($"[VC] Failed to create microphone for '{displayName}': object is null");
			else
				VoiceChatPluginMain.Logger.LogInfo($"[VC] Microphone set to '{displayName}', instance created.");
		}
		catch (Exception ex)
		{
			VoiceChatPluginMain.Logger.LogError($"[VC] Exception while setting microphone to '{deviceName}': {ex.Message}");
			_interstellar.Microphone = null; // Ensure no invalid object is used
		}
	}

#if !ANDROID
	public void SetSpeaker(string deviceName)
	{
		try
		{
			_interstellar.Speaker = new WindowsSpeaker(deviceName);
			string displayName = string.IsNullOrEmpty(deviceName) ? "(default)" : deviceName;
			if (_interstellar.Speaker == null)
				VoiceChatPluginMain.Logger.LogError($"[VC] Failed to create speaker for '{displayName}': object is null");
			else
				VoiceChatPluginMain.Logger.LogInfo($"[VC] Speaker set to '{displayName}', instance created.");
		}
		catch (Exception ex)
		{
			VoiceChatPluginMain.Logger.LogError($"[VC] Exception while setting speaker to '{deviceName}': {ex.Message}");
			_interstellar.Speaker = null;
		}
	}
#endif

	public void Update()
	{
		TryUpdateLocalProfile();

		var localPlayer = PlayerControl.LocalPlayer;
		Vector2? listenerPos = localPlayer ? (Vector2)localPlayer.transform.position : null;

		List<SpeakerCache> speakerCache = new();
		if (listenerPos.HasValue)
		{
			const float maxRange = 6f;
			foreach (var v in _virtualSpeakers)
			{
				float d = Vector2.Distance(v.Position, listenerPos.Value);
				if (d < maxRange)
					speakerCache.Add(new(v,
						GetVolume(d, maxRange),
						GetPan(listenerPos.Value.x, v.Position.x)));
			}
		}

		var inLobby = LobbyBehaviour.Instance != null;
		var inMeeting = MeetingHud.Instance != null || ExileController.Instance != null;
		var inGame = ShipStatus.Instance != null;

		// Optional per-frame check for microphone and speaker validity (avoid frequent logging)
		if (_interstellar.Microphone == null)
			VoiceChatPluginMain.Logger.LogWarning("[VC] Microphone is null, cannot capture audio.");
		if (_interstellar.Speaker == null)
			VoiceChatPluginMain.Logger.LogWarning("[VC] Speaker is null, cannot play audio.");

		foreach (var client in _clients.Values)
		{
			if (inLobby || !inGame)
				client.UpdateLobby();
			else if (inMeeting)
				client.UpdateMeeting();
			else
				client.UpdateTaskPhase(listenerPos, speakerCache, _virtualMics);
		}
	}

	public void Rejoin()
	{
		_interstellar.Rejoin();
		UpdateLocalProfile(always: true);
		foreach (var c in _clients.Values) c.ResetMapping();
	}

	public void Close()
	{
		_interstellar.Disconnect();
	}

	public bool TryGetPlayer(byte playerId, [MaybeNullWhen(false)] out VCPlayer player)
	{
		foreach (var c in _clients.Values)
		{
			if (c.PlayerId == playerId) { player = c; return true; }
		}
		player = null;
		return false;
	}

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
			VoiceChatPluginMain.Logger.LogInfo($"[VC] Updated local profile: playerId={_lastId}, name={_lastName}");
		}
	}

	internal static float GetVolume(float distance, float hearDistance)
		=> Math.Clamp(1f - distance / hearDistance, 0f, 1f);

	internal static float GetPan(float micX, float speakerX)
		=> Math.Clamp((speakerX - micX) / 3f, -1f, 1f);

	internal record SpeakerCache(IVoiceComponent Speaker, float Volume, float Pan);
}
