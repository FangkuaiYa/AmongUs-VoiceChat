using HarmonyLib;
using TMPro;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
public static class MeetingSpeakingIndicatorPatch
{
	private const float SpeakingThreshold = 0.01f;
	private static readonly Dictionary<byte, TextMeshPro> Indicators = new();

	public static void Postfix(MeetingHud __instance)
	{
		if (__instance.playerStates == null)
		{
			HideAll();
			return;
		}

		var room = VoiceChatRoom.Current;
		if (room == null)
		{
			HideAll();
			return;
		}

		HashSet<byte> speakingPlayers = new();
		foreach (var client in room.AllClients)
		{
			if (client.PlayerId == byte.MaxValue) continue;
			if (client.Level > SpeakingThreshold)
				speakingPlayers.Add(client.PlayerId);
		}

		HashSet<byte> aliveIndicators = new();
		foreach (var state in __instance.playerStates)
		{
			if (state == null) continue;
			aliveIndicators.Add(state.TargetPlayerId);

			var indicator = GetOrCreateIndicator(state);
			indicator.gameObject.SetActive(speakingPlayers.Contains(state.TargetPlayerId));
		}

		CleanupStale(aliveIndicators);
	}

	[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
	private static class MeetingHudDestroyPatch
	{
		private static void Postfix()
		{
			foreach (var obj in Indicators.Values)
			{
				if (obj != null) Object.Destroy(obj.gameObject);
			}
			Indicators.Clear();
		}
	}

	private static TextMeshPro GetOrCreateIndicator(PlayerVoteArea state)
	{
		if (Indicators.TryGetValue(state.TargetPlayerId, out var existing) && existing != null)
			return existing;

		var template = state.NameText;
		if (template == null)
		{
			var go = new GameObject("VC_SpeakingIndicator");
			go.transform.SetParent(state.transform, false);
			var tmp = go.AddComponent<TextMeshPro>();
			tmp.text = VoiceChatLocalization.Tr("speaking");
			tmp.fontSize = 2f;
			tmp.color = Color.green;
			tmp.alignment = TextAlignmentOptions.Center;
			go.transform.localPosition = new Vector3(-0.52f, 0.21f, -1f);
			Indicators[state.TargetPlayerId] = tmp;
			return tmp;
		}

		var indicatorObj = Object.Instantiate(template.gameObject, state.transform);
		var indicator = indicatorObj.GetComponent<TextMeshPro>();
		indicator.name = "VC_SpeakingIndicator";
		indicator.text = VoiceChatLocalization.Tr("speaking");
		indicator.color = Color.green;
		indicator.alignment = TextAlignmentOptions.Center;
		indicator.enableWordWrapping = false;
		indicator.fontSize = template.fontSize * 0.9f;
		indicator.transform.localPosition = new Vector3(-0.52f, 0.21f, -1f);
		indicator.transform.localScale = template.transform.localScale * 0.8f;
		indicatorObj.SetActive(false);
		Indicators[state.TargetPlayerId] = indicator;
		return indicator;
	}

	private static void HideAll()
	{
		foreach (var obj in Indicators.Values)
		{
			if (obj != null) obj.gameObject.SetActive(false);
		}
	}

	private static void CleanupStale(HashSet<byte> aliveIndicators)
	{
		List<byte> toRemove = new();
		foreach (var kv in Indicators)
		{
			if (aliveIndicators.Contains(kv.Key)) continue;
			if (kv.Value != null) Object.Destroy(kv.Value.gameObject);
			toRemove.Add(kv.Key);
		}

		foreach (var key in toRemove)
			Indicators.Remove(key);
	}
}
