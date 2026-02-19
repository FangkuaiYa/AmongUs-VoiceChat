using Hazel;
using HarmonyLib;

namespace VoiceChatPlugin.VoiceChat;

public sealed class VoiceChatRoomSettings
{
	private const byte RpcId = 201;

	public bool CanTalkThroughWalls { get; private set; }
	public float MaxChatDistance { get; private set; }

	public VoiceChatRoomSettings(bool canTalkThroughWalls, float maxChatDistance)
	{
		CanTalkThroughWalls = canTalkThroughWalls;
		MaxChatDistance = ClampDistance(maxChatDistance);
	}

	public void Apply(bool canTalkThroughWalls, float maxChatDistance)
	{
		CanTalkThroughWalls = canTalkThroughWalls;
		MaxChatDistance = ClampDistance(maxChatDistance);
	}

	public void Apply(VoiceChatRoomSettings other)
		=> Apply(other.CanTalkThroughWalls, other.MaxChatDistance);

	public VoiceChatRoomSettings Clone()
		=> new(CanTalkThroughWalls, MaxChatDistance);

	private static float ClampDistance(float value)
		=> Math.Clamp(value, 1.5f, 20f);

	public static void SendToAll(VoiceChatRoomSettings settings)
	{
		if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;

		MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
			PlayerControl.LocalPlayer.NetId,
			RpcId,
			SendOption.Reliable,
			-1);
		writer.Write(settings.CanTalkThroughWalls);
		writer.Write(settings.MaxChatDistance);
		AmongUsClient.Instance.FinishRpcImmediately(writer);
	}

	[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
	public static class VoiceChatRoomSettingsRpcPatch
	{
		[HarmonyPostfix]
		public static void Postfix(byte callId, MessageReader reader)
		{
			if (callId != RpcId) return;
			bool canTalkThroughWalls = reader.ReadBoolean();
			float maxDistance = reader.ReadSingle();
			VoiceChatConfig.ApplySyncedRoomSettings(canTalkThroughWalls, maxDistance);
			VoiceChatPluginMain.Logger.LogInfo($"[VC] Received room settings from RPC: throughWalls={canTalkThroughWalls}, maxDistance={maxDistance:0.0}");
		}
	}
}
