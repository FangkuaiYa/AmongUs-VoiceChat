using HarmonyLib;
using TMPro;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

/// <summary>
/// 在会议投票 UI 中，正在说话的玩家名字旁显示「说话中」指示符。
/// 修复：也对本地玩家显示（通过 LocalMicLevel 检测）。
/// </summary>
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
public static class MeetingSpeakingIndicatorPatch
{
	private const float SpeakingThreshold = 0.01f;
	private static readonly Dictionary<byte, TextMeshPro> Indicators = new();

	public static void Postfix(MeetingHud __instance)
	{
		if (__instance.playerStates == null) { HideAll(); return; }

		var room = VoiceChatRoom.Current;
		if (room == null) { HideAll(); return; }

		// 收集正在说话的玩家
		HashSet<byte> speaking = new();
		foreach (var c in room.AllClients)
		{
			if (c.PlayerId != byte.MaxValue && c.Level > SpeakingThreshold)
				speaking.Add(c.PlayerId);
		}
		// 本地玩家
		if (PlayerControl.LocalPlayer && room.LocalMicLevel > SpeakingThreshold
		    && PlayerControl.LocalPlayer.PlayerId != byte.MaxValue)
			speaking.Add(PlayerControl.LocalPlayer.PlayerId);

		HashSet<byte> alive = new();
		foreach (var state in __instance.playerStates)
		{
			if (state == null) continue;
			alive.Add(state.TargetPlayerId);
			var ind = GetOrCreate(state);
			ind.gameObject.SetActive(speaking.Contains(state.TargetPlayerId));
		}

		CleanStale(alive);
	}

	[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
	private static class DestroyPatch
	{
		private static void Postfix()
		{
			foreach (var v in Indicators.Values)
				if (v != null) Object.Destroy(v.gameObject);
			Indicators.Clear();
		}
	}

	private static TextMeshPro GetOrCreate(PlayerVoteArea state)
	{
		if (Indicators.TryGetValue(state.TargetPlayerId, out var ex) && ex != null)
			return ex;

		var template = state.NameText;
		TextMeshPro tmp;

		if (template == null)
		{
			var go = new GameObject("VC_SpeakingIndicator");
			go.transform.SetParent(state.transform, false);
			tmp           = go.AddComponent<TextMeshPro>();
			tmp.fontSize  = 2f;
			tmp.color     = Color.green;
			tmp.alignment = TextAlignmentOptions.Center;
			go.transform.localPosition = new Vector3(-0.52f, 0.21f, -1f);
		}
		else
		{
			var go = Object.Instantiate(template.gameObject, state.transform);
			tmp = go.GetComponent<TextMeshPro>();
			tmp.name                = "VC_SpeakingIndicator";
			tmp.color               = Color.green;
			tmp.alignment           = TextAlignmentOptions.Center;
			tmp.enableWordWrapping  = false;
			tmp.fontSize            = template.fontSize * 0.9f;
			tmp.transform.localPosition = new Vector3(-0.52f, 0.21f, -1f);
			tmp.transform.localScale    = template.transform.localScale * 0.8f;
		}

		tmp.text = VoiceChatLocalization.Tr("speaking");
		tmp.gameObject.SetActive(false);
		Indicators[state.TargetPlayerId] = tmp;
		return tmp;
	}

	private static void HideAll()
	{
		foreach (var v in Indicators.Values)
			if (v != null) v.gameObject.SetActive(false);
	}

	private static void CleanStale(HashSet<byte> alive)
	{
		List<byte> remove = new();
		foreach (var kv in Indicators)
		{
			if (alive.Contains(kv.Key)) continue;
			if (kv.Value != null) Object.Destroy(kv.Value.gameObject);
			remove.Add(kv.Key);
		}
		foreach (var k in remove) Indicators.Remove(k);
	}
}
