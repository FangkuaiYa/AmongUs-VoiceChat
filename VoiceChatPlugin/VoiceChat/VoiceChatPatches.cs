using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceChatPatches
{
	private static PassiveButton? _micToggleButton;
	private static GameObject? _micToggleButtonObject;
	private static PassiveButton? _speakerToggleButton;
	private static GameObject? _speakerToggleButtonObject;
	private static AspectPosition? _micButtonAspect;
	private static AspectPosition? _speakerButtonAspect;
	private static readonly AspectPosition.EdgeAlignments VoiceButtonAnchor = AspectPosition.EdgeAlignments.RightTop;
	private static readonly Vector3 MicDistanceFromEdge = new(3.85f, 0.55f, 0f);
	private static readonly Vector3 SpeakerDistanceFromEdge = new(4.50f, 0.55f, 0f);

	private static bool _micMuted;
	private static bool _impostorMode;
	private static bool _speakerMuted;

	public static bool IsSpeakerMuted => _speakerMuted;
	public static bool IsImpostorRadioOnly => _impostorMode;

	private static VoiceChatRoomSettings? _lastSentSettings;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
	static void HudUpdate_Post(HudManager __instance)
	{
		EnsureHudButtons(__instance);
		UpdateHudButtons(__instance);

		if (VoiceChatRoom.Current == null) return;

		if (_speakerMuted)
			VoiceChatRoom.Current.SetMasterVolume(0f);

		TrySyncHostRoomSettingsIfChanged();

		try { VoiceChatRoom.Current.Update(); }
		catch (Exception ex) { VoiceChatPluginMain.Logger.LogError("[VC] Update error: " + ex); }
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
	static void OnGameJoined_Post(AmongUsClient __instance)
	{
		if (AmongUsClient.Instance == null) return;

		if (__instance.networkAddress is "127.0.0.1" or "localhost") return;

		string region = __instance.networkAddress;
		string roomId = __instance.GameId.ToString();

		VoiceChatPluginMain.Logger.LogInfo($"[VC] Starting voice chat – region={region} room={roomId}");
		VoiceChatRoom.Start(region, roomId);

		if (VoiceChatRoom.Current != null)
		{
			ApplyMicStateToVoiceChat();
		}

		if (__instance.AmHost)
		{
			VoiceChatConfig.ApplyLocalHostSettingsToSynced();
			_lastSentSettings = null;
			TrySyncHostRoomSettingsIfChanged();
		}

		if (_speakerMuted && VoiceChatRoom.Current != null)
			VoiceChatRoom.Current.SetMasterVolume(0f);
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame))]
	static void ExitGame_Post()
	{
		VoiceChatPluginMain.Logger.LogInfo("[VC] Closing voice chat room.");
		_lastSentSettings = null;
		VoiceChatRoom.CloseCurrentRoom();
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
	static void KeyboardUpdate_Post()
	{
		if (Input.GetKeyDown(KeyCode.M))
		{
			ToggleMic();
		}
	}

	private static void EnsureHudButtons(HudManager hud)
	{
		if (hud.MapButton == null) return;

		if (_micToggleButtonObject == null || _micToggleButton == null)
		{
			_micToggleButtonObject = Object.Instantiate(hud.MapButton.gameObject, hud.transform.parent);
			_micToggleButtonObject.name = "VC_MicToggleButton";

			HideButtonBackground(_micToggleButtonObject);

			var micIconObj = new GameObject("VCIcon");
			micIconObj.transform.SetParent(_micToggleButtonObject.transform, false);
			micIconObj.transform.localPosition = Vector3.zero;
			var micIconSr = micIconObj.AddComponent<SpriteRenderer>();
			micIconSr.sprite = LoadSpriteFromResources("VoiceChatPlugin.Resources.MicOn.png", 900f);
			micIconSr.sortingOrder = 5;

			_micToggleButton = _micToggleButtonObject.GetComponent<PassiveButton>();
			_micToggleButton.OnClick = new ButtonClickedEvent();
			_micToggleButton.OnClick.AddListener((Action)ToggleMic);

			_micButtonAspect = _micToggleButtonObject.GetComponent<AspectPosition>();
			if (_micButtonAspect == null) _micButtonAspect = _micToggleButtonObject.AddComponent<AspectPosition>();
			_micButtonAspect.Alignment = VoiceButtonAnchor;
			_micButtonAspect.DistanceFromEdge = MicDistanceFromEdge;
		}

		if (_speakerToggleButtonObject == null || _speakerToggleButton == null)
		{
			_speakerToggleButtonObject = Object.Instantiate(hud.MapButton.gameObject, hud.transform.parent);
			_speakerToggleButtonObject.name = "VC_SpeakerToggleButton";

			HideButtonBackground(_speakerToggleButtonObject);

			var spkIconObj = new GameObject("VCIcon");
			spkIconObj.transform.SetParent(_speakerToggleButtonObject.transform, false);
			spkIconObj.transform.localPosition = Vector3.zero;
			var spkIconSr = spkIconObj.AddComponent<SpriteRenderer>();
			spkIconSr.sprite = LoadSpriteFromResources("VoiceChatPlugin.Resources.SpeakerOn.png", 900f);
			spkIconSr.sortingOrder = 5;

			_speakerToggleButton = _speakerToggleButtonObject.GetComponent<PassiveButton>();
			_speakerToggleButton.OnClick = new ButtonClickedEvent();
			_speakerToggleButton.OnClick.AddListener((Action)ToggleSpeaker);

			_speakerButtonAspect = _speakerToggleButtonObject.GetComponent<AspectPosition>();
			if (_speakerButtonAspect == null) _speakerButtonAspect = _speakerToggleButtonObject.AddComponent<AspectPosition>();
			_speakerButtonAspect.Alignment = VoiceButtonAnchor;
			_speakerButtonAspect.DistanceFromEdge = SpeakerDistanceFromEdge;
		}
	}

	private static void HideButtonBackground(GameObject buttonObj)
	{
		foreach (var sr in buttonObj.GetComponentsInChildren<SpriteRenderer>())
			sr.color = new Color(0f, 0f, 0f, 0f);
	}

	public static Dictionary<string, Sprite> CachedSprites = new();

	public static Sprite LoadSpriteFromResources(string path, float pixelsPerUnit, bool cache = true)
	{
		try
		{
			if (cache && CachedSprites.TryGetValue(path + pixelsPerUnit, out var sprite)) return sprite;
			Texture2D texture = LoadTextureFromResources(path);
			if (texture == null) return null!;
			sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
			if (cache) sprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
			if (!cache) return sprite;
			return CachedSprites[path + pixelsPerUnit] = sprite;
		}
		catch
		{
			System.Console.WriteLine("Error loading sprite from path: " + path);
		}
		return null!;
	}

	public static Sprite loadSpriteFromResources(string path, float pixelsPerUnit, bool cache = true)
		=> LoadSpriteFromResources(path, pixelsPerUnit, cache);

	public static unsafe Texture2D LoadTextureFromResources(string path)
	{
		try
		{
			Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, true);
			Assembly assembly = Assembly.GetExecutingAssembly();
			Stream? stream = assembly.GetManifestResourceStream(path);
			if (stream == null) return null!;
			var length = stream.Length;
			var byteTexture = new Il2CppStructArray<byte>(length);
			stream.Read(new Span<byte>(IntPtr.Add(byteTexture.Pointer, IntPtr.Size * 4).ToPointer(), (int)length));
			ImageConversion.LoadImage(texture, byteTexture, false);
			return texture;
		}
		catch
		{
			System.Console.WriteLine("Error loading texture from resources: " + path);
		}
		return null!;
	}

	public static unsafe Texture2D loadTextureFromResources(string path)
		=> LoadTextureFromResources(path);

	private static void UpdateHudButtons(HudManager hud)
	{
		if (_micToggleButtonObject == null || _speakerToggleButtonObject == null) return;

		bool active = !(MapBehaviour.Instance && MapBehaviour.Instance.IsOpen);
		_micToggleButtonObject.SetActive(active);
		_speakerToggleButtonObject.SetActive(active);

		_micButtonAspect ??= _micToggleButtonObject.GetComponent<AspectPosition>();
		_speakerButtonAspect ??= _speakerToggleButtonObject.GetComponent<AspectPosition>();
		if (_micButtonAspect != null)
		{
			_micButtonAspect.Alignment = VoiceButtonAnchor;
			_micButtonAspect.DistanceFromEdge = MicDistanceFromEdge;
		}
		if (_speakerButtonAspect != null)
		{
			_speakerButtonAspect.Alignment = VoiceButtonAnchor;
			_speakerButtonAspect.DistanceFromEdge = SpeakerDistanceFromEdge;
		}
		_micButtonAspect?.AdjustPosition();
		_speakerButtonAspect?.AdjustPosition();

		RefreshButtonVisuals();
	}

	/// <summary>
	/// 麦克风三态循环：
	/// 正常（全员语音）→ 内鬼频道 → 静音 → 正常
	/// </summary>
	private static void ToggleMic()
	{
		string oldState = _micMuted ? "静音" : (_impostorMode ? "内鬼频道" : "正常");

		bool canUseImpostorMode = PlayerControl.LocalPlayer != null &&
						  PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true &&
						  !PlayerControl.LocalPlayer.Data.IsDead;

		if (!_micMuted && !_impostorMode)
		{
			if (canUseImpostorMode)
			{
				_impostorMode = true;
				_micMuted = false;
			}
			else
			{
				_impostorMode = false;
				_micMuted = true;
			}
		}
		else if (!_micMuted && _impostorMode)
		{
			_impostorMode = false;
			_micMuted = true;
		}
		else if (_micMuted && !_impostorMode)
		{
			_micMuted = false;
			_impostorMode = false;
		}

		ApplyMicStateToVoiceChat();

		string newState = _micMuted ? "静音" : (_impostorMode ? "内鬼频道" : "正常");
		VoiceChatPluginMain.Logger.LogInfo($"[VC] Mic: {oldState} → {newState}");
		RefreshButtonVisuals();
	}

	/// <summary>
	/// 将当前麦克风状态应用到语音房间（静音/内鬼模式）
	/// </summary>
	private static void ApplyMicStateToVoiceChat()
	{
		if (VoiceChatRoom.Current == null) return;
		VoiceChatRoom.Current.SetMute(_micMuted);
	}

	private static void ToggleSpeaker()
	{
		_speakerMuted = !_speakerMuted;
		if (VoiceChatRoom.Current != null)
		{
			VoiceChatRoom.Current.SetMasterVolume(_speakerMuted ? 0f : VoiceChatConfig.MasterVolume);
		}
		VoiceChatPluginMain.Logger.LogInfo("[VC] Speaker " + (_speakerMuted ? "OFF" : "ON"));
		RefreshButtonVisuals();
	}

	private static void RefreshButtonVisuals()
	{
		if (_micToggleButtonObject != null)
		{
			var iconTransform = _micToggleButtonObject.transform.Find("VCIcon");
			var renderer = iconTransform != null ? iconTransform.GetComponent<SpriteRenderer>() : null;
			if (renderer != null)
			{
				if (_micMuted)
				{
					renderer.sprite = LoadSpriteFromResources("VoiceChatPlugin.Resources.MicOff.png", 900f);
					renderer.color = new Color(1f, 0.45f, 0.45f, 1f);
				}
				else if (_impostorMode)
				{
					renderer.sprite = LoadSpriteFromResources("VoiceChatPlugin.Resources.MicOn.png", 900f);
					renderer.color = new Color(0.5f, 0.9f, 1f, 1f);
				}
				else
				{
					renderer.sprite = LoadSpriteFromResources("VoiceChatPlugin.Resources.MicOn.png", 900f);
					renderer.color = Color.white; 
				}
			}
		}

		if (_speakerToggleButtonObject != null)
		{
			var iconTransform = _speakerToggleButtonObject.transform.Find("VCIcon");
			var renderer = iconTransform != null ? iconTransform.GetComponent<SpriteRenderer>() : null;
			if (renderer != null)
			{
				renderer.sprite = LoadSpriteFromResources(
					_speakerMuted
						? "VoiceChatPlugin.Resources.SpeakerOff.png"
						: "VoiceChatPlugin.Resources.SpeakerOn.png",
					900f);
				renderer.color = _speakerMuted ? new Color(1f, 0.45f, 0.45f, 1f) : Color.white;
			}
		}
	}

	/// <summary>
	/// 仅在房间设置实际发生变化时才向所有客户端同步，避免无意义的网络广播。
	/// </summary>
	private static void TrySyncHostRoomSettingsIfChanged()
	{
		if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
		if (AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Joined) return;

		var current = VoiceChatConfig.SyncedRoomSettings;

		if (_lastSentSettings != null
			&& _lastSentSettings.CanTalkThroughWalls == current.CanTalkThroughWalls
			&& Mathf.Approximately(_lastSentSettings.MaxChatDistance, current.MaxChatDistance))
			return;

		VoiceChatRoomSettings.SendToAll(current);
		_lastSentSettings = new VoiceChatRoomSettings(current.CanTalkThroughWalls, current.MaxChatDistance);
		VoiceChatPluginMain.Logger.LogInfo($"[VC] Synced room settings to all: throughWalls={current.CanTalkThroughWalls}, maxDistance={current.MaxChatDistance:0.0}");
	}

	public static void MarkRoomSettingsDirty()
	{
		_lastSentSettings = null;
	}
}
}