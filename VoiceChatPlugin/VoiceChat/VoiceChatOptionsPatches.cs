using HarmonyLib;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;
using VoiceChatPlugin;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceChatOptionsPatches
{
	private static GameObject? _popUp;
	private static ToggleButtonBehaviour? _buttonPrefab;

	private static readonly List<string> _micDevices = new();
	private static readonly List<string> _speakerDevices = new();

	private const string VoiceOptionsButtonName = "VoiceChatOptionsButton";

	// ─── 布局常量 ─────────────────────────────────────────────────────────────
	// 面板在 Among Us 世界坐标系中高度约 ±2.5，宽度约 ±2.2
	// 为避免内容超出背景边界，所有行都限制在 y ∈ [-2.0, 2.1] 范围内
	private const float RowHeight = 0.55f;  // 每行高度
	private const float SectionGap = 0.15f;  // 分区间额外间距
	private const float TopY = 2.5f;  // 第一行（标题）y，不超过背景顶边

	[HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
	[HarmonyPostfix]
	public static void OptionsMenuBehaviour_StartPostfix(OptionsMenuBehaviour __instance)
	{
		if (!__instance.CensorChatButton) return;

		if (_buttonPrefab == null)
		{
			_buttonPrefab = Object.Instantiate(__instance.CensorChatButton);
			Object.DontDestroyOnLoad(_buttonPrefab);
			_buttonPrefab.name = "VoiceChatOptionPrefab";
			_buttonPrefab.gameObject.SetActive(false);
		}

		if (_popUp == null)
			CreateOptionsPanel(__instance);

		InitializeEntryButton(__instance);
	}

	private static void CreateOptionsPanel(OptionsMenuBehaviour prefab)
	{
		_popUp = Object.Instantiate(prefab.gameObject);
		Object.DontDestroyOnLoad(_popUp);

		var panelTransform = _popUp.transform;
		var pos = panelTransform.localPosition;
		pos.z = -860f;
		panelTransform.localPosition = pos;

		Object.Destroy(_popUp.GetComponent<OptionsMenuBehaviour>());

		for (int i = 0; i < _popUp.transform.childCount; i++)
		{
			var child = _popUp.transform.GetChild(i).gameObject;
			if (child.name is not ("Background" or "CloseButton"))
				Object.Destroy(child);
		}

		// 美化面板背景色
		var bg = _popUp.transform.Find("Background")?.GetComponent<SpriteRenderer>();
		if (bg != null)
			bg.color = new Color32(12, 14, 22, 240);

		var closeButton = _popUp.GetComponentInChildren<PassiveButton>();
		if (closeButton != null)
		{
			closeButton.OnClick = new ButtonClickedEvent();
			closeButton.OnClick.AddListener((Action)(() =>
			{
				_popUp.SetActive(false);
			}));
		}

		_popUp.SetActive(false);
	}

	private static void InitializeEntryButton(OptionsMenuBehaviour instance)
	{
		var parent = instance.CensorChatButton.transform.parent;
		var existing = parent.Find(VoiceOptionsButtonName);
		if (existing != null)
			Object.Destroy(existing.gameObject);

		var entryButton = Object.Instantiate(_buttonPrefab!, parent);
		entryButton.name = VoiceOptionsButtonName;
		var gameState = AmongUsClient.Instance != null ? AmongUsClient.Instance.GameState : InnerNet.InnerNetClient.GameStates.NotJoined;
		entryButton.transform.localPosition = gameState == InnerNet.InnerNetClient.GameStates.NotJoined ? new Vector3(-1.34f, 2.99f, 0f) : new Vector3(-1.94f, -1.58f, 0f);
		entryButton.transform.localScale = new Vector3(0.49f, 0.82f, 1f);

		entryButton.Text.text = "VC";
		entryButton.Text.transform.localScale = new Vector3(1.8f, 0.95f, 1f);
		entryButton.gameObject.SetActive(true);

		var passiveButton = entryButton.GetComponent<PassiveButton>();
		passiveButton.OnClick = new ButtonClickedEvent();
		passiveButton.OnClick.AddListener((Action)(() =>
		{
			if (_popUp == null) return;

			bool closeUnderlying = false;
			if (instance.transform.parent != null && instance.transform.parent == HudManager.Instance.transform)
			{
				_popUp.transform.SetParent(HudManager.Instance.transform);
				_popUp.transform.localPosition = new Vector3(0f, 0f, -860f);
				closeUnderlying = true;
			}
			else
			{
				_popUp.transform.SetParent(null);
				Object.DontDestroyOnLoad(_popUp);
			}

			RefreshOptionsView();
			if (closeUnderlying)
				instance.Close();
		}));
	}

	private static void RefreshOptionsView()
	{
		if (_popUp == null || _buttonPrefab == null) return;

		_popUp.SetActive(false);
		_popUp.SetActive(true);

		for (int i = _popUp.transform.childCount - 1; i >= 0; i--)
		{
			var child = _popUp.transform.GetChild(i).gameObject;
			if (child.name is "Background" or "CloseButton") continue;
			Object.Destroy(child);
		}

		RefreshDeviceCaches();

		// ─── 线性从顶到底布局，超出底部限制则停止渲染 ─────────────────────────
		float y = TopY;

		// 标题
		CreateHeader(y);
		y -= RowHeight + SectionGap;

		// ── 音频设备区 ────────────────────────────────────────────────────────
		CreateSectionLabel(VoiceChatLocalization.Tr("audioDevices"), y);
		y -= RowHeight * 0.70f;

		CreateVolumeRow(VoiceChatLocalization.Tr("micVolume"), y, VoiceChatConfig.MicVolume, v =>
		{
			VoiceChatConfig.SetMicVolume(v);
			VoiceChatRoom.Current?.SetMicVolume(v);
		});
		y -= RowHeight;

		CreateCycleRow(VoiceChatLocalization.Tr("microphone"), y, _micDevices, ToDisplayValue(VoiceChatConfig.MicrophoneDevice),
			selected =>
			{
				var value = FromDisplayValue(selected);
				VoiceChatConfig.SetMicrophoneDevice(value);
				VoiceChatRoom.Current?.SetMicrophone(value);
				VoiceChatRoom.Current?.SetMicVolume(VoiceChatConfig.MicVolume);
			});
		y -= RowHeight;

		CreateVolumeRow(VoiceChatLocalization.Tr("speakerVolume"), y, VoiceChatConfig.MasterVolume, v =>
		{
			VoiceChatConfig.SetMasterVolume(v);
			VoiceChatRoom.Current?.SetMasterVolume(v);
		});
		y -= RowHeight;

#if !ANDROID
		CreateCycleRow(VoiceChatLocalization.Tr("speaker"), y, _speakerDevices, ToDisplayValue(VoiceChatConfig.SpeakerDevice),
			selected =>
			{
				var value = FromDisplayValue(selected);
				VoiceChatConfig.SetSpeakerDevice(value);
				VoiceChatRoom.Current?.SetSpeaker(value);
			});
		y -= RowHeight;
#endif

		// ── 房间设置区 ────────────────────────────────────────────────────────
		y -= SectionGap;
		CreateHostRoomSettingsSection(ref y);
	}

	private static void CreateHostRoomSettingsSection(ref float y)
	{
		bool isHost = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
		var synced = VoiceChatConfig.SyncedRoomSettings;

		CreateSectionLabel(VoiceChatLocalization.Tr("roomSettings"), y);
		y -= RowHeight * 0.70f;

		// 只读信息行（所有玩家均可见）
		string wallText = synced.CanTalkThroughWalls ? VoiceChatLocalization.Tr("passThrough") : VoiceChatLocalization.Tr("blocked");
		CreateValueDisplay(y, VoiceChatLocalization.Tr("walls"), wallText);
		y -= RowHeight;

		CreateValueDisplay(y, VoiceChatLocalization.Tr("maxDistance"), $"{synced.MaxChatDistance:0.0}");
		y -= RowHeight;

		// 主机专属操作按钮
		if (!isHost) return;

		y -= SectionGap * 0.5f;

		float btnY = y;

		// Walls 切换按钮（左侧）
		CreateWideActionButton(
			synced.CanTalkThroughWalls ? VoiceChatLocalization.Tr("wallsPass") : VoiceChatLocalization.Tr("wallsBlock"),
			new Vector3(-1.25f, btnY, -0.5f),
			new Color32(38, 110, 75, 255),
			() =>
			{
				VoiceChatConfig.SetHostCanTalkThroughWalls(!VoiceChatConfig.HostCanTalkThroughWalls);
				ApplyAndBroadcastHostRoomSettings();
			});

		// Distance − 按钮（中间）
		CreateWideActionButton(VoiceChatLocalization.Tr("distanceMinus"),
			new Vector3(0f, btnY, -0.5f),
			new Color32(70, 55, 120, 255),
			() =>
			{
				float next = Mathf.Clamp(VoiceChatConfig.HostMaxChatDistance - 0.5f, 1.5f, 20f);
				VoiceChatConfig.SetHostMaxChatDistance(next);
				ApplyAndBroadcastHostRoomSettings();
			});

		// Distance + 按钮（右侧）
		CreateWideActionButton(VoiceChatLocalization.Tr("distancePlus"),
			new Vector3(1.25f, btnY, -0.5f),
			new Color32(70, 55, 120, 255),
			() =>
			{
				float next = Mathf.Clamp(VoiceChatConfig.HostMaxChatDistance + 0.5f, 1.5f, 20f);
				VoiceChatConfig.SetHostMaxChatDistance(next);
				ApplyAndBroadcastHostRoomSettings();
			});

		y -= RowHeight;
	}

	/// <summary>
	/// 主机主动修改房间设置后立即广播，并通知 VoiceChatPatches 更新脏标记，
	/// 避免 Update 中的变更检测再次重复发送。
	/// </summary>
	private static void ApplyAndBroadcastHostRoomSettings()
	{
		VoiceChatConfig.ApplyLocalHostSettingsToSynced();
		VoiceChatRoomSettings.SendToAll(VoiceChatConfig.SyncedRoomSettings);
		// 通知 VoiceChatPatches 记录本次发送的值，避免下一帧再次触发同步
		VoiceChatPatches.MarkRoomSettingsDirty(); // 实际上 Mark 后会立即被覆盖为已同步状态
		VoiceChatPluginMain.Logger.LogInfo(
			$"[VC] Host updated room settings: throughWalls={VoiceChatConfig.SyncedRoomSettings.CanTalkThroughWalls}, " +
			$"maxDistance={VoiceChatConfig.SyncedRoomSettings.MaxChatDistance:0.0}");
		RefreshOptionsView();
	}

	private static void RefreshDeviceCaches()
	{
		_micDevices.Clear();
		_micDevices.Add(VoiceChatLocalization.Tr("default"));
		try
		{
			for (int i = 0; i < WaveInEvent.DeviceCount; i++)
			{
				var capability = WaveInEvent.GetCapabilities(i);
				if (!string.IsNullOrWhiteSpace(capability.ProductName))
					_micDevices.Add(capability.ProductName);
			}
		}
		catch (Exception ex)
		{
			VoiceChatPluginMain.Logger.LogWarning("[VC] Failed to enumerate microphone devices: " + ex.Message);
		}

		_speakerDevices.Clear();
		_speakerDevices.Add(VoiceChatLocalization.Tr("default"));
#if !ANDROID
		try
		{
			using var enumerator = new MMDeviceEnumerator();
			foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
			{
				if (!string.IsNullOrWhiteSpace(dev.FriendlyName))
					_speakerDevices.Add(dev.FriendlyName);
			}
		}
		catch (Exception ex)
		{
			VoiceChatPluginMain.Logger.LogWarning("[VC] Failed to enumerate speaker devices: " + ex.Message);
		}
#endif
	}

	// ─── UI 构建辅助方法 ─────────────────────────────────────────────────────

	private static void CreateHeader(float y)
	{
		var header = InstantiateButton();
		header.transform.FindChild("Background").gameObject.SetActive(false);
        header.transform.FindChild("Text_TMP").transform.localScale = new Vector3(1.1f, 1.3f, 1f);
        header.transform.localPosition = new Vector3(0f, y, -0.5f);
		header.transform.localScale = new Vector3(1.24f, 0.95f, 1f);
		header.Background.color = new Color32(18, 22, 42, 255);
		header.Text.text = VoiceChatLocalization.Tr("header");
		header.Text.fontSizeMin = header.Text.fontSizeMax = 1.55f;
		header.Text.alignment = TextAlignmentOptions.Center;
		header.Text.color = new Color32(175, 215, 255, 255);
		SetSpriteSize(header, new Vector2(4.2f, 0.55f));
		SetupPassive(header, () => { });
	}

	private static void CreateSectionLabel(string text, float y)
	{
		var label = InstantiateButton();
        label.transform.GetChild(0).gameObject.SetActive(false);
        label.transform.localPosition = new Vector3(-1.85f, y, -0.5f);
		label.transform.localScale = new Vector3(1.05f, 1.05f, 1f);
		label.Background.color = new Color32(28, 36, 58, 200);
		label.Text.text = text;
		label.Text.fontSizeMin = label.Text.fontSizeMax = 1.1f;
		label.Text.alignment = TextAlignmentOptions.Left;
		label.Text.color = new Color32(130, 165, 220, 255);
		SetSpriteSize(label, new Vector2(3.0f, 0.42f));
		SetupPassive(label, () => { });
	}

	private static void CreateVolumeRow(string label, float y, float value, Action<float> onChanged)
	{
		CreateValueDisplay(y, label, $"{value:0.00}");
		CreateSmallActionButton("−", new Vector3(-1.87f, y, -0.5f), () =>
		{
			onChanged(Mathf.Clamp(value - 0.1f, 0.1f, 2f));
			RefreshOptionsView();
		});
		CreateSmallActionButton("+", new Vector3(1.87f, y, -0.5f), () =>
		{
			onChanged(Mathf.Clamp(value + 0.1f, 0.1f, 2f));
			RefreshOptionsView();
		});
	}

	private static void CreateCycleRow(string label, float y, IReadOnlyList<string> values, string current, Action<string> onChanged)
	{
		if (values.Count == 0) return;

		int index = IndexOf(values, current);
		if (index < 0) index = 0;

		string display = values[index] == VoiceChatLocalization.Tr("default") ? VoiceChatLocalization.Tr("default") : Shorten(values[index], 22);
		CreateValueDisplay(y, label, display);

		CreateSmallActionButton("◀", new Vector3(-1.87f, y, -0.5f), () =>
		{
			int next = (index - 1 + values.Count) % values.Count;
			onChanged(values[next]);
			RefreshOptionsView();
		});
		CreateSmallActionButton("▶", new Vector3(1.87f, y, -0.5f), () =>
		{
			int next = (index + 1) % values.Count;
			onChanged(values[next]);
			RefreshOptionsView();
		});
	}

	private static void CreateValueDisplay(float y, string label, string value)
	{
		var display = InstantiateButton();
		display.transform.localPosition = new Vector3(0f, y, -0.5f);
		display.transform.localScale = new Vector3(0.82f, 0.82f, 1f);
		display.Background.color = new Color32(24, 30, 50, 255);
		display.Text.text = $"<color=#8aaae5>{label}</color>:  <color=#ffffff>{value}</color>";
		display.Text.fontSizeMin = display.Text.fontSizeMax = 1.25f;
		display.Text.alignment = TextAlignmentOptions.Center;
		SetSpriteSize(display, new Vector2(3.4f, 0.50f));
		SetupPassive(display, () => { });
	}

	private static void CreateSmallActionButton(string text, Vector3 position, Action onClick)
	{
		var button = InstantiateButton();
		button.transform.localPosition = position;
		button.transform.localScale = new Vector3(0.62f, 0.82f, 1f);
		button.Background.color = new Color32(52, 64, 98, 255);
		button.Text.text = text;
		button.Text.fontSizeMin = button.Text.fontSizeMax = 1.9f;
		button.Text.alignment = TextAlignmentOptions.Center;
		button.Text.color = new Color32(200, 215, 255, 255);
		SetSpriteSize(button, new Vector2(0.80f, 0.50f));
		SetupPassive(button, onClick, new Color32(80, 100, 148, 255));
	}

	private static void CreateWideActionButton(string text, Vector3 position, Color32 baseColor, Action onClick)
	{
		var hover = new Color32(
			(byte)Mathf.Clamp(baseColor.r + 28, 0, 255),
			(byte)Mathf.Clamp(baseColor.g + 28, 0, 255),
			(byte)Mathf.Clamp(baseColor.b + 28, 0, 255),
			255);

		var button = InstantiateButton();
		button.transform.localPosition = position;
		button.transform.localScale = new Vector3(0.88f, 0.78f, 1f);
		button.Background.color = baseColor;
		button.Text.text = text;
		button.Text.fontSizeMin = button.Text.fontSizeMax = 1.1f;
		button.Text.alignment = TextAlignmentOptions.Center;
		button.Text.color = new Color32(230, 240, 255, 255);
		SetSpriteSize(button, new Vector2(1.45f, 0.48f));
		SetupPassive(button, onClick, hover);
	}

	private static ToggleButtonBehaviour InstantiateButton()
	{
		var button = Object.Instantiate(_buttonPrefab!, _popUp!.transform);
		button.onState = false;
		button.gameObject.SetActive(true);
		return button;
	}

	private static void SetSpriteSize(ToggleButtonBehaviour button, Vector2 size)
	{
		foreach (var renderer in button.gameObject.GetComponentsInChildren<SpriteRenderer>())
			renderer.size = size;
	}

	private static void SetupPassive(ToggleButtonBehaviour button, Action onClick, Color32? hoverColor = null)
	{
		var passive = button.GetComponent<PassiveButton>();
		passive.OnClick = new ButtonClickedEvent();
		passive.OnMouseOut = new UnityEvent();
		passive.OnMouseOver = new UnityEvent();

		var defaultColor = button.Background.color;
		var highlight = hoverColor ?? defaultColor;

		passive.OnClick.AddListener((Action)(() => onClick()));
		passive.OnMouseOver.AddListener((Action)(() => button.Background.color = highlight));
		passive.OnMouseOut.AddListener((Action)(() => button.Background.color = defaultColor));
	}

	// ─── 工具 ─────────────────────────────────────────────────────────────────

	private static string ToDisplayValue(string raw)
		=> string.IsNullOrEmpty(raw) ? VoiceChatLocalization.Tr("default") : raw;

	private static string FromDisplayValue(string selected)
		=> selected == VoiceChatLocalization.Tr("default") ? "" : selected;

	private static string Shorten(string value, int maxLength)
	{
		if (value.Length <= maxLength) return value;
		return value[..(maxLength - 3)] + "...";
	}

	private static int IndexOf(IReadOnlyList<string> values, string current)
	{
		for (int i = 0; i < values.Count; i++)
		{
			if (values[i] == current) return i;
		}
		return -1;
	}
}
