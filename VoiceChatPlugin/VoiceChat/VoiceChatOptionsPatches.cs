using System;
using System.Collections.Generic;
using HarmonyLib;
using VoiceChatPlugin.NAudio.CoreAudioApi;
using VoiceChatPlugin.NAudio.Wave;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceChatOptionsPatches
{
	private static GameObject? _popUp;
	private static ToggleButtonBehaviour? _buttonPrefab;

	private static readonly List<string> _micDevices = new();
	private static readonly List<string> _spkDevices = new();

	private const string ButtonName = "VoiceChatOptionsButton";
	private const float RowH = 0.54f;
	private const float SecGap = 0.13f;
	private const float TopY = 2.46f;

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

	private static void BuildPopup(OptionsMenuBehaviour src)
	{
		_popUp = Object.Instantiate(src.gameObject);
		Object.DontDestroyOnLoad(_popUp);
		var tf = _popUp.transform;
		var pos = tf.localPosition; pos.z = -860f; tf.localPosition = pos;

		Object.Destroy(_popUp.GetComponent<OptionsMenuBehaviour>());

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

	private static void AddEntryButton(OptionsMenuBehaviour inst)
	{
		var parent = inst.CensorChatButton.transform.parent;
		var existing = parent.Find(ButtonName);
		if (existing != null) Object.Destroy(existing.gameObject);

		var btn = Object.Instantiate(_buttonPrefab!, parent);
		btn.name = ButtonName;

		bool inGame = AmongUsClient.Instance?.GameState == InnerNet.InnerNetClient.GameStates.Joined;
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
				Refresh();
				inst.Close();
			}
			else
			{
				_popUp.transform.SetParent(null);
				Object.DontDestroyOnLoad(_popUp);
				Refresh();
			}
		}));
	}

	private static void Refresh()
	{
		if (_popUp == null || _buttonPrefab == null) return;

		for (int i = _popUp.transform.childCount - 1; i >= 0; i--)
		{
			var ch = _popUp.transform.GetChild(i).gameObject;
			if (ch.name is "Background" or "CloseButton") continue;
			Object.Destroy(ch);
		}

		if (!_popUp.activeSelf) _popUp.SetActive(true);

		RefreshDeviceCaches();

		float y = TopY;
		MakeHeader(ref y);
		DrawAudioPage(ref y);
	}

	private static void MakeHeader(ref float y)
	{
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

		y -= RowH + SecGap;
	}

	private static void DrawAudioPage(ref float y)
	{
		MakeSectionLabel(VoiceChatLocalization.Tr("audioDevices"), y);
		y -= RowH * 0.65f;

		MakeVolumeRow(VoiceChatLocalization.Tr("micVolume"), y, VoiceChatConfig.MicVolume, v =>
		{
			VoiceChatConfig.SetMicVolume(v);
			VoiceChatRoom.Current?.SetMicVolume(v);
		});
		y -= RowH;

		MakeCycleRow(VoiceChatLocalization.Tr("microphone"), y, _micDevices,
			ToDisplay(VoiceChatConfig.MicrophoneDevice), sel =>
			{
				var v = FromDisplay(sel);
				VoiceChatConfig.SetMicrophoneDevice(v);
				VoiceChatRoom.Current?.SetMicrophone(v);
				VoiceChatRoom.Current?.SetMicVolume(VoiceChatConfig.MicVolume);
			});
		y -= RowH;

		MakeVolumeRow(VoiceChatLocalization.Tr("speakerVolume"), y, VoiceChatConfig.MasterVolume, v =>
		{
			VoiceChatConfig.SetMasterVolume(v);
			VoiceChatRoom.Current?.SetMasterVolume(v);
		});
		y -= RowH;

		MakeCycleRow(VoiceChatLocalization.Tr("speaker"), y, _spkDevices,
			ToDisplay(VoiceChatConfig.SpeakerDevice), sel =>
			{
				var v = FromDisplay(sel);
				VoiceChatConfig.SetSpeakerDevice(v);
				VoiceChatRoom.Current?.SetSpeaker(v);
			});
		y -= RowH;
	}

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
		try
		{
			using var e = new MMDeviceEnumerator();
			foreach (var dev in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
				if (!string.IsNullOrWhiteSpace(dev.FriendlyName))
					_spkDevices.Add(dev.FriendlyName);
		}
		catch { }
	}

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

	private static void MakeSmallBtn(string text, Vector3 pos, Action onClick, Color32? color = null)
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