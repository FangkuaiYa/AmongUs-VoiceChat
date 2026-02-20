using HarmonyLib;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// VC 设置面板：分两页
///   Page 0 — 音频设备 + 常用房间规则（隔墙、距离、仅视线）
///   Page 1 — 其余房间规则（内鬼听幽灵、通风口可听等）
/// </summary>
[HarmonyPatch]
public static class VoiceChatOptionsPatches
{
	private static GameObject? _popUp;
	private static ToggleButtonBehaviour? _buttonPrefab;

	private static readonly List<string> _micDevices = new();
	private static readonly List<string> _spkDevices = new();

	private const string ButtonName = "VoiceChatOptionsButton";
	private const float RowH = 0.54f;   // 行高
	private const float SecGap = 0.13f;   // 节间额外间距
	private const float TopY = 2.46f;   // 第一行 Y

	private static int _page; // 0 = 设备+部分规则, 1 = 其余规则

	// ── 入口 ──────────────────────────────────────────────────────────
	[HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
	[HarmonyPostfix]
	public static void OnOptionsMenuStart(OptionsMenuBehaviour __instance)
	{
		if (!__instance.CensorChatButton) return;

		if (_buttonPrefab == null)
		{
			_buttonPrefab = Object.Instantiate(__instance.CensorChatButton);
			Object.DontDestroyOnLoad(_buttonPrefab);
			_buttonPrefab.name = "VC_ButtonPrefab";
			_buttonPrefab.gameObject.SetActive(false);
		}

		if (_popUp == null) BuildPopup(__instance);
		AddEntryButton(__instance);
	}

	// ── 弹出窗口构建 ──────────────────────────────────────────────────
	private static void BuildPopup(OptionsMenuBehaviour src)
	{
		_popUp = Object.Instantiate(src.gameObject);
		Object.DontDestroyOnLoad(_popUp);
		var tf = _popUp.transform;
		var pos = tf.localPosition; pos.z = -860f; tf.localPosition = pos;

		Object.Destroy(_popUp.GetComponent<OptionsMenuBehaviour>());

		// 只保留 Background 和 CloseButton
		for (int i = tf.childCount - 1; i >= 0; i--)
		{
			var ch = tf.GetChild(i).gameObject;
			if (ch.name is not ("Background" or "CloseButton"))
				Object.Destroy(ch);
		}

		var bg = tf.Find("Background")?.GetComponent<SpriteRenderer>();
		if (bg != null) bg.color = new Color32(12, 14, 22, 242);

		var close = _popUp.GetComponentInChildren<PassiveButton>();
		if (close != null)
		{
			close.OnClick = new ButtonClickedEvent();
			close.OnClick.AddListener((Action)(() => _popUp!.SetActive(false)));
		}

		_popUp.SetActive(false);
	}

	// ── 入口按钮 ──────────────────────────────────────────────────────
	private static void AddEntryButton(OptionsMenuBehaviour inst)
	{
		var parent = inst.CensorChatButton.transform.parent;
		var existing = parent.Find(ButtonName);
		if (existing != null) Object.Destroy(existing.gameObject);

		var btn = Object.Instantiate(_buttonPrefab!, parent);
		btn.name = ButtonName;

		bool inGame = AmongUsClient.Instance?.GameState
			== InnerNet.InnerNetClient.GameStates.Joined;
		btn.transform.localPosition = inGame
			? new Vector3(-1.94f, -1.58f, 0f)
			: new Vector3(-1.34f, 2.99f, 0f);
		btn.transform.localScale = new Vector3(0.49f, 0.82f, 1f);
		btn.Text.text = "VC";
		btn.Text.transform.localScale = new Vector3(1.8f, 0.95f, 1f);
		btn.gameObject.SetActive(true);

		var pb = btn.GetComponent<PassiveButton>();
		pb.OnClick = new ButtonClickedEvent();
		pb.OnClick.AddListener((Action)(() =>
		{
			if (_popUp == null) return;
			if (inst.transform.parent != null && inst.transform.parent == HudManager.Instance?.transform)
			{
				_popUp.transform.SetParent(HudManager.Instance.transform);
				_popUp.transform.localPosition = new Vector3(0f, 0f, -860f);
				inst.Close();
			}
			else
			{
				_popUp.transform.SetParent(null);
				Object.DontDestroyOnLoad(_popUp);
			}
			_page = 0;
			Refresh();
		}));
	}

	// ── 刷新面板 ──────────────────────────────────────────────────────
	private static void Refresh()
	{
		if (_popUp == null || _buttonPrefab == null) return;
		_popUp.SetActive(false);
		_popUp.SetActive(true);

		// 清除旧内容
		for (int i = _popUp.transform.childCount - 1; i >= 0; i--)
		{
			var ch = _popUp.transform.GetChild(i).gameObject;
			if (ch.name is "Background" or "CloseButton") continue;
			Object.Destroy(ch);
		}

		RefreshDeviceCaches();

		float y = TopY;
		MakeHeader(ref y);

		if (_page == 0) DrawDevicePage(ref y);
		else DrawRulesPage(ref y);
	}

	// ── 页头（含标题 + 翻页按钮）─────────────────────────────────────
	private static void MakeHeader(ref float y)
	{
		// 标题
		var title = MakeBtn();
		title.transform.Find("Background")?.gameObject.SetActive(false);
		title.transform.localPosition = new Vector3(0f, y, -0.5f);
		title.transform.localScale = new Vector3(1.24f, 0.95f, 1f);
		title.Text.text = VoiceChatLocalization.Tr("header");
		title.Text.fontSizeMin = title.Text.fontSizeMax = 1.55f;
		title.Text.alignment = TextAlignmentOptions.Center;
		title.Text.color = new Color32(175, 215, 255, 255);
		SpriteSize(title, new Vector2(3.0f, 0.55f));
		Passive(title, () => { });

		// 翻页按钮（右侧）
		string pageLabel = _page == 0
			? VoiceChatLocalization.Tr("pageDevices")
			: VoiceChatLocalization.Tr("pageRules");
		MakeSmallBtn(pageLabel, new Vector3(1.7f, y, -0.5f), () =>
		{
			_page = 1 - _page;
			Refresh();
		}, new Color32(60, 80, 130, 255));

		y -= RowH + SecGap;
	}

	// ── 第0页：音频设备 + 常用房间规则（隔墙、距离、仅视线） ───────────
	private static void DrawDevicePage(ref float y)
	{
		// 音频设备节
		MakeSectionLabel(VoiceChatLocalization.Tr("audioDevices"), y);
		y -= RowH * 0.65f;

		// 麦克风音量
		MakeVolumeRow(VoiceChatLocalization.Tr("micVolume"), y, VoiceChatConfig.MicVolume, v =>
		{
			VoiceChatConfig.SetMicVolume(v);
			VoiceChatRoom.Current?.SetMicVolume(v);
		});
		y -= RowH;

		// 麦克风选择
		MakeCycleRow(VoiceChatLocalization.Tr("microphone"), y, _micDevices,
			ToDisplay(VoiceChatConfig.MicrophoneDevice), sel =>
			{
				var v = FromDisplay(sel);
				VoiceChatConfig.SetMicrophoneDevice(v);
				VoiceChatRoom.Current?.SetMicrophone(v);
				VoiceChatRoom.Current?.SetMicVolume(VoiceChatConfig.MicVolume);
			});
		y -= RowH;

		// 扬声器音量
		MakeVolumeRow(VoiceChatLocalization.Tr("speakerVolume"), y, VoiceChatConfig.MasterVolume, v =>
		{
			VoiceChatConfig.SetMasterVolume(v);
			VoiceChatRoom.Current?.SetMasterVolume(v);
		});
		y -= RowH;

#if !ANDROID
		// 扬声器选择
		MakeCycleRow(VoiceChatLocalization.Tr("speaker"), y, _spkDevices,
			ToDisplay(VoiceChatConfig.SpeakerDevice), sel =>
			{
				var v = FromDisplay(sel);
				VoiceChatConfig.SetSpeakerDevice(v);
				VoiceChatRoom.Current?.SetSpeaker(v);
			});
		y -= RowH;
#endif

		// ── 常用房间规则节（隔墙、距离、仅视线） ──
		y -= SecGap;
		MakeSectionLabel(VoiceChatLocalization.Tr("roomSettings"), y);
		y -= RowH * 0.65f;

		var s = VoiceChatConfig.SyncedRoomSettings;
		bool isHost = AmongUsClient.Instance?.AmHost == true;

		// 隔墙语音（开关形式）
		MakeFlagRow(VoiceChatLocalization.Tr("walls"), y, s.WallsBlockSound, isHost, () =>
		{
			VoiceChatConfig.SetHostWallsBlockSound(!VoiceChatConfig.HostWallsBlockSound);
			BroadcastAndRefresh();
		});
		y -= RowH;

		// 最大距离（带加减按钮，仅主机可见）
		MakeDistanceRow(y, s.MaxChatDistance, isHost);
		y -= RowH;

		// 仅视线内可听
		MakeFlagRow(VoiceChatLocalization.Tr("onlyHearInSight"), y, s.OnlyHearInSight, isHost, () =>
		{
			VoiceChatConfig.SetHostOnlyHearInSight(!VoiceChatConfig.HostOnlyHearInSight);
			BroadcastAndRefresh();
		});
		y -= RowH;

		// 注意：内鬼可听幽灵已移至第二页
	}

	// ── 第1页：其余房间规则（包含内鬼听幽灵及其他） ───────────────────
	private static void DrawRulesPage(ref float y)
	{
		bool isHost = AmongUsClient.Instance?.AmHost == true;
		var s = VoiceChatConfig.SyncedRoomSettings;

		MakeSectionLabel(VoiceChatLocalization.Tr("roomSettings"), y);
		y -= RowH * 0.65f;

		// 内鬼可听幽灵（从第一页移回）
		MakeFlagRow(VoiceChatLocalization.Tr("impostorHearGhosts"), y, s.ImpostorHearGhosts, isHost, () =>
		{
			VoiceChatConfig.SetHostImpostorHearGhosts(!VoiceChatConfig.HostImpostorHearGhosts);
			BroadcastAndRefresh();
		});
		y -= RowH;

		// 以下为其余规则
		MakeFlagRow(VoiceChatLocalization.Tr("hearInVent"), y, s.HearInVent,
			isHost, () => { VoiceChatConfig.SetHostHearInVent(!VoiceChatConfig.HostHearInVent); BroadcastAndRefresh(); });
		y -= RowH;

		MakeFlagRow(VoiceChatLocalization.Tr("ventPrivateChat"), y, s.VentPrivateChat,
			isHost, () => { VoiceChatConfig.SetHostVentPrivateChat(!VoiceChatConfig.HostVentPrivateChat); BroadcastAndRefresh(); });
		y -= RowH;

		MakeFlagRow(VoiceChatLocalization.Tr("commsSabDisables"), y, s.CommsSabDisables,
			isHost, () => { VoiceChatConfig.SetHostCommsSabDisables(!VoiceChatConfig.HostCommsSabDisables); BroadcastAndRefresh(); });
		y -= RowH;

		MakeFlagRow(VoiceChatLocalization.Tr("cameraCanHear"), y, s.CameraCanHear,
			isHost, () => { VoiceChatConfig.SetHostCameraCanHear(!VoiceChatConfig.HostCameraCanHear); BroadcastAndRefresh(); });
		y -= RowH;

		MakeFlagRow(VoiceChatLocalization.Tr("impostorPrivateRadio"), y, s.ImpostorPrivateRadio,
			isHost, () => { VoiceChatConfig.SetHostImpostorPrivateRadio(!VoiceChatConfig.HostImpostorPrivateRadio); BroadcastAndRefresh(); });
		y -= RowH;

		MakeFlagRow(VoiceChatLocalization.Tr("onlyGhostsCanTalk"), y, s.OnlyGhostsCanTalk,
			isHost, () => { VoiceChatConfig.SetHostOnlyGhostsCanTalk(!VoiceChatConfig.HostOnlyGhostsCanTalk); BroadcastAndRefresh(); });
		y -= RowH;

		MakeFlagRow(VoiceChatLocalization.Tr("onlyMeetingOrLobby"), y, s.OnlyMeetingOrLobby,
			isHost, () => { VoiceChatConfig.SetHostOnlyMeetingOrLobby(!VoiceChatConfig.HostOnlyMeetingOrLobby); BroadcastAndRefresh(); });
		y -= RowH;
	}

	// ── 广播并刷新面板 ────────────────────────────────────────────────
	private static void BroadcastAndRefresh()
	{
		VoiceChatConfig.ApplyLocalHostSettingsToSynced();
		VoiceChatRoomSettings.SendToAll(VoiceChatConfig.SyncedRoomSettings);
		VoiceChatPatches.MarkRoomSettingsDirty();
		Refresh();
	}

	// ── 设备枚举 ──────────────────────────────────────────────────────
	private static void RefreshDeviceCaches()
	{
		_micDevices.Clear();
		_micDevices.Add(VoiceChatLocalization.Tr("default"));
		try
		{
			for (int i = 0; i < WaveInEvent.DeviceCount; i++)
			{
				var cap = WaveInEvent.GetCapabilities(i);
				if (!string.IsNullOrWhiteSpace(cap.ProductName))
					_micDevices.Add(cap.ProductName);
			}
		}
		catch { }

		_spkDevices.Clear();
		_spkDevices.Add(VoiceChatLocalization.Tr("default"));
#if !ANDROID
		try
		{
			using var e = new MMDeviceEnumerator();
			foreach (var dev in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
				if (!string.IsNullOrWhiteSpace(dev.FriendlyName))
					_spkDevices.Add(dev.FriendlyName);
		}
		catch { }
#endif
	}

	// ── UI 构件 ────────────────────────────────────────────────────────
	private static void MakeVolumeRow(string label, float y, float value, Action<float> onChange)
	{
		MakeValueDisplay(y, label, $"{value:0.00}");
		MakeSmallBtn("−", new Vector3(-1.87f, y, -0.5f),
			() => { onChange(Mathf.Clamp(value - 0.1f, 0.1f, 2f)); Refresh(); });
		MakeSmallBtn("+", new Vector3(1.87f, y, -0.5f),
			() => { onChange(Mathf.Clamp(value + 0.1f, 0.1f, 2f)); Refresh(); });
	}

	private static void MakeCycleRow(string label, float y,
		IReadOnlyList<string> values, string current, Action<string> onChange)
	{
		if (values.Count == 0) return;
		int idx = IndexOf(values, current);
		if (idx < 0) idx = 0;
		string disp = values[idx] == VoiceChatLocalization.Tr("default")
			? VoiceChatLocalization.Tr("default")
			: Shorten(values[idx], 22);
		MakeValueDisplay(y, label, disp);
		MakeSmallBtn("◀", new Vector3(-1.87f, y, -0.5f), () =>
		{
			onChange(values[(idx - 1 + values.Count) % values.Count]);
			Refresh();
		});
		MakeSmallBtn("▶", new Vector3(1.87f, y, -0.5f), () =>
		{
			onChange(values[(idx + 1) % values.Count]);
			Refresh();
		});
	}

	/// <summary>布尔开关行：非 Host 时只展示状态，Host 时可点击切换</summary>
	private static void MakeFlagRow(string label, float y, bool value, bool isHost, Action onToggle)
	{
		string mark = value ? $"<color=#55ff55>{VoiceChatLocalization.Tr("flagOn")}</color>" : $"<color=#ff5555>{VoiceChatLocalization.Tr("flagOff")}</color>";
		MakeValueDisplay(y, label, mark);

		if (!isHost) return;
		MakeSmallBtn(value ? VoiceChatLocalization.Tr("flagOn") : VoiceChatLocalization.Tr("flagOff"),
			new Vector3(1.87f, y, -0.5f),
			onToggle,
			value ? new Color32(38, 110, 75, 255) : new Color32(100, 40, 40, 255));
	}

	/// <summary>距离调节行（仅主机可见加减按钮）</summary>
	private static void MakeDistanceRow(float y, float value, bool isHost)
	{
		MakeValueDisplay(y, VoiceChatLocalization.Tr("maxDistance"), $"{value:0.0}");
		if (!isHost) return;
		MakeSmallBtn("−", new Vector3(-1.87f, y, -0.5f), () =>
		{
			float newVal = Mathf.Clamp(value - 0.5f, 1.5f, 20f);
			VoiceChatConfig.SetHostMaxChatDistance(newVal);
			BroadcastAndRefresh();
		});
		MakeSmallBtn("+", new Vector3(1.87f, y, -0.5f), () =>
		{
			float newVal = Mathf.Clamp(value + 0.5f, 1.5f, 20f);
			VoiceChatConfig.SetHostMaxChatDistance(newVal);
			BroadcastAndRefresh();
		});
	}

	private static void MakeSectionLabel(string text, float y)
	{
		var lbl = MakeBtn();
		lbl.transform.GetChild(0).gameObject.SetActive(false);
		lbl.transform.localPosition = new Vector3(-1.85f, y, -0.5f);
		lbl.transform.localScale = new Vector3(1.05f, 1.05f, 1f);
		lbl.Text.text = text;
		lbl.Text.fontSizeMin = lbl.Text.fontSizeMax = 1.1f;
		lbl.Text.alignment = TextAlignmentOptions.Left;
		lbl.Text.color = new Color32(130, 165, 220, 255);
		SpriteSize(lbl, new Vector2(3.0f, 0.42f));
		Passive(lbl, () => { });
	}

	private static void MakeValueDisplay(float y, string label, string value)
	{
		var d = MakeBtn();
		d.transform.localPosition = new Vector3(0f, y, -0.5f);
		d.transform.localScale = new Vector3(0.82f, 0.82f, 1f);
		d.Background.color = new Color32(24, 30, 50, 255);
		d.Text.text = $"<color=#8aaae5>{label}</color>:  <color=#ffffff>{value}</color>";
		d.Text.fontSizeMin = d.Text.fontSizeMax = 1.2f;
		d.Text.alignment = TextAlignmentOptions.Center;
		SpriteSize(d, new Vector2(3.4f, 0.48f));
		Passive(d, () => { });
	}

	private static void MakeSmallBtn(string text, Vector3 pos, Action onClick,
		Color32? color = null)
	{
		var b = MakeBtn();
		b.transform.localPosition = pos;
		b.transform.localScale = new Vector3(0.62f, 0.82f, 1f);
		b.Background.color = color ?? new Color32(52, 64, 98, 255);
		b.Text.text = text;
		b.Text.fontSizeMin = b.Text.fontSizeMax = 1.6f;
		b.Text.alignment = TextAlignmentOptions.Center;
		b.Text.color = new Color32(200, 215, 255, 255);
		SpriteSize(b, new Vector2(0.80f, 0.46f));
		Passive(b, onClick, new Color32(
			(byte)Mathf.Clamp((b.Background.color.r * 255) + 28, 0, 255),
			(byte)Mathf.Clamp((b.Background.color.g * 255) + 28, 0, 255),
			(byte)Mathf.Clamp((b.Background.color.b * 255) + 28, 0, 255),
			255));
	}

	// 保留原 MakeWideBtn 以备不时之需，但当前未使用
	private static void MakeWideBtn(string text, Vector3 pos, Color32 baseColor, Action onClick)
	{
		var hover = new Color32(
			(byte)Mathf.Clamp(baseColor.r + 28, 0, 255),
			(byte)Mathf.Clamp(baseColor.g + 28, 0, 255),
			(byte)Mathf.Clamp(baseColor.b + 28, 0, 255), 255);
		var b = MakeBtn();
		b.transform.localPosition = pos;
		b.transform.localScale = new Vector3(0.88f, 0.78f, 1f);
		b.Background.color = baseColor;
		b.Text.text = text;
		b.Text.fontSizeMin = b.Text.fontSizeMax = 1.05f;
		b.Text.alignment = TextAlignmentOptions.Center;
		b.Text.color = new Color32(230, 240, 255, 255);
		SpriteSize(b, new Vector2(1.45f, 0.46f));
		Passive(b, onClick, hover);
	}

	private static ToggleButtonBehaviour MakeBtn()
	{
		var b = Object.Instantiate(_buttonPrefab!, _popUp!.transform);
		b.onState = false;
		b.gameObject.SetActive(true);
		return b;
	}

	private static void SpriteSize(ToggleButtonBehaviour b, Vector2 size)
	{
		foreach (var sr in b.GetComponentsInChildren<SpriteRenderer>())
			sr.size = size;
	}

	private static void Passive(ToggleButtonBehaviour b, Action onClick, Color32? hover = null)
	{
		var pb = b.GetComponent<PassiveButton>();
		pb.OnClick = new ButtonClickedEvent();
		pb.OnMouseOut = new UnityEvent();
		pb.OnMouseOver = new UnityEvent();
		var def = b.Background.color;
		var hl = hover ?? def;
		pb.OnClick.AddListener((Action)(() => onClick()));
		pb.OnMouseOver.AddListener((Action)(() => b.Background.color = hl));
		pb.OnMouseOut.AddListener((Action)(() => b.Background.color = def));
	}

	private static string ToDisplay(string raw)
		=> string.IsNullOrEmpty(raw) ? VoiceChatLocalization.Tr("default") : raw;
	private static string FromDisplay(string sel)
		=> sel == VoiceChatLocalization.Tr("default") ? "" : sel;
	private static string Shorten(string v, int max)
		=> v.Length <= max ? v : v[..(max - 3)] + "...";
	private static int IndexOf(IReadOnlyList<string> list, string cur)
	{
		for (int i = 0; i < list.Count; i++) if (list[i] == cur) return i;
		return -1;
	}
}