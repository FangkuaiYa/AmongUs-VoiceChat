using HarmonyLib;
using TMPro;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin
{
	[HarmonyPatch(typeof(PingTracker), nameof(PingTracker.Update))]
	public static class PingTrackerPatch
	{
		private const float SpeakingThreshold = 0.01f;
		private static readonly AspectPosition.EdgeAlignments SpeakingAnchor = AspectPosition.EdgeAlignments.LeftTop;
		private static readonly Vector3 SpeakingDistanceFromEdge = new(0.5f, 0.11f, 0f);
		private static PingTracker? _speakingTracker;
		private static AspectPosition? _speakingAspect;
		private static string _baseText = string.Empty;
		private static bool _baseCaptured;

		static void Postfix(PingTracker __instance)
		{
			if (__instance == null || __instance.text == null)
				return;

			if (_speakingTracker == null || _speakingTracker.gameObject == null)
				CreateSpeakingTracker(__instance);
			if (_speakingTracker == null || _speakingTracker.text == null)
				return;

			if (!_baseCaptured)
			{
				_baseText = __instance.text.text;
				_baseCaptured = true;
			}

			var isStarted = AmongUsClient.Instance != null && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started;
			var room = VoiceChatRoom.Current;
			if (room == null)
			{
				_speakingTracker.gameObject.SetActive(false);
				return;
			}

			List<string> speakers = new();
			foreach (var client in room.AllClients)
			{
				if (client.Level > SpeakingThreshold)
					speakers.Add(client.PlayerName);
			}

			_speakingTracker.gameObject.SetActive(true);
			_speakingTracker.text.text = speakers.Count > 0
				?"<color=#00FF00FF>Speaking: " + string.Join(", ", speakers) + "</color>"
				: "";

			if (_speakingAspect != null)
			{
				_speakingAspect.Alignment = SpeakingAnchor;
				_speakingAspect.DistanceFromEdge = SpeakingDistanceFromEdge;
				_speakingAspect.AdjustPosition();
			}
		}

		private static void CreateSpeakingTracker(PingTracker template)
		{
			var speakingObject = Object.Instantiate(template.gameObject, template.transform.parent);
			speakingObject.name = "VC_SpeakingPingTracker";
			speakingObject.SetActive(true);
			_speakingTracker = speakingObject.GetComponent<PingTracker>();
			if (_speakingTracker?.text != null)
			{
				_speakingTracker.text.gameObject.SetActive(true);
				_speakingTracker.text.alignment = TextAlignmentOptions.TopLeft;
				_speakingTracker.text.enableWordWrapping = false;
			}

			_speakingAspect = speakingObject.GetComponent<AspectPosition>();
			if (_speakingAspect == null)
				_speakingAspect = speakingObject.AddComponent<AspectPosition>();
			_speakingAspect.Alignment = SpeakingAnchor;
			_speakingAspect.DistanceFromEdge = SpeakingDistanceFromEdge;
			_speakingAspect.AdjustPosition();
		}
	}
}
