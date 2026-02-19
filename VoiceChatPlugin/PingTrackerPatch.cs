using HarmonyLib;
using TMPro;
using System.Linq;
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

		static void Postfix(PingTracker __instance)
		{
			if (__instance?.text == null)
				return;

			if (_speakingTracker == null || _speakingTracker.gameObject == null)
				CreateSpeakingTracker(__instance);
			if (_speakingTracker?.text == null)
				return;

			var tracker = _speakingTracker;
			if (VoiceChatPatches.IsSpeakerMuted)
			{
				tracker?.gameObject?.SetActive(true);
				tracker?.text.text = string.Empty;
				return;
			}

			var room = VoiceChatRoom.Current;
			if (room == null)
			{
				tracker?.gameObject?.SetActive(false);
				return;
			}

			List<string> speakers = new();
			HashSet<byte> vcInstalledPlayers = new();
			foreach (var client in room.AllClients)
			{
				if (client.PlayerId != byte.MaxValue)
					vcInstalledPlayers.Add(client.PlayerId);
				if (client.Level > SpeakingThreshold)
					speakers.Add(client.PlayerName);
			}

			if (PlayerControl.LocalPlayer && room.LocalMicLevel > SpeakingThreshold)
				speakers.Add(PlayerControl.LocalPlayer.Data?.PlayerName ?? PlayerControl.LocalPlayer.name);

			List<string> noVcPlayers = new();
			byte localPlayerId = PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : byte.MaxValue;
			foreach (var player in PlayerControl.AllPlayerControls)
			{
				if (!player || player.Data == null) continue;
				if (player.PlayerId == localPlayerId) continue;
				if (!vcInstalledPlayers.Contains(player.PlayerId))
					noVcPlayers.Add(player.Data.PlayerName);
			}

			string speakingLabel = VoiceChatLocalization.Tr("speaking");
			string noVcLabel = string.Format(VoiceChatLocalization.Tr("missingPlayers"),
				noVcPlayers.Count > 0 ? string.Join(", ", noVcPlayers.Distinct()) : "-");
			string speakingText = speakers.Count > 0
				? $"<color=#00FF00FF>{speakingLabel}: {string.Join(", ", speakers.Distinct())}</color>"
				: string.Empty;
			string missingText = $"<color=#FFD35AFF>{noVcLabel}</color>"; 
			tracker?.gameObject?.SetActive(true);
			tracker?.text.text = string.IsNullOrEmpty(speakingText)
				? missingText
				: speakingText + "\n" + missingText;

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

			_speakingAspect = speakingObject.GetComponent<AspectPosition>() ?? speakingObject.AddComponent<AspectPosition>();
			_speakingAspect.Alignment = SpeakingAnchor;
			_speakingAspect.DistanceFromEdge = SpeakingDistanceFromEdge;
			_speakingAspect.AdjustPosition();
		}
	}
}
