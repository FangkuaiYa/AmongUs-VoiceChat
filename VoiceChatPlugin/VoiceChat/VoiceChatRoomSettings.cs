using Hazel;
using HarmonyLib;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// 房主持有、通过 RPC 同步给所有客户端的房间语音规则。
/// 对应截图两张图里的全部开关选项。
/// </summary>
public sealed class VoiceChatRoomSettings
{
	private const byte RpcId = 201;

	// ── 基本距离/穿墙 ──────────────────────────────────────────────────────
	/// <summary>最大听声距离</summary>
	public float MaxChatDistance     { get; internal set; }
	/// <summary>墙壁可以隔音（true = 隔音，false = 可穿墙）</summary>
	public bool  WallsBlockSound     { get; internal set; }
	/// <summary>仅能听见视野内的人</summary>
	public bool  OnlyHearInSight     { get; internal set; }

	// ── 幽灵 / 死亡 ────────────────────────────────────────────────────────
	/// <summary>内鬼能听见幽灵</summary>
	public bool  ImpostorHearGhosts  { get; internal set; }
	/// <summary>只有幽灵可以语音（活人全静音）</summary>
	public bool  OnlyGhostsCanTalk   { get; internal set; }

	// ── 管道 ────────────────────────────────────────────────────────────────
	/// <summary>能听见管道里的人（在管道内的玩家声音不被屏蔽）</summary>
	public bool  HearInVent          { get; internal set; }
	/// <summary>管道内私聊（在管道内的人只能跟同管道的人说话）</summary>
	public bool  VentPrivateChat     { get; internal set; }

	// ── 破坏/监控 ──────────────────────────────────────────────────────────
	/// <summary>破坏通讯时禁用语音</summary>
	public bool  CommsSabDisables    { get; internal set; }
	/// <summary>监控可以收听声音</summary>
	public bool  CameraCanHear       { get; internal set; }

	// ── 内鬼频道 ───────────────────────────────────────────────────────────
	/// <summary>内鬼私密通话（内鬼之间用无线电效果，活人听不见）</summary>
	public bool  ImpostorPrivateRadio { get; internal set; }

	// ── 仅会议/大厅 ────────────────────────────────────────────────────────
	/// <summary>仅限会议/大厅内语音（任务阶段全静音）</summary>
	public bool  OnlyMeetingOrLobby  { get; internal set; }

	// 向后兼容
	public bool CanTalkThroughWalls => !WallsBlockSound;

	// ── 构造 ────────────────────────────────────────────────────────────────
	public VoiceChatRoomSettings() { Reset(); }

	public void Reset()
	{
		MaxChatDistance      = 6f;
		WallsBlockSound      = true;
		OnlyHearInSight      = false;
		ImpostorHearGhosts   = false;
		OnlyGhostsCanTalk    = false;
		HearInVent           = true;
		VentPrivateChat      = false;
		CommsSabDisables     = true;
		CameraCanHear        = true;
		ImpostorPrivateRadio = false;
		OnlyMeetingOrLobby   = false;
	}

	public void Apply(VoiceChatRoomSettings o)
	{
		MaxChatDistance      = Math.Clamp(o.MaxChatDistance, 1.5f, 20f);
		WallsBlockSound      = o.WallsBlockSound;
		OnlyHearInSight      = o.OnlyHearInSight;
		ImpostorHearGhosts   = o.ImpostorHearGhosts;
		OnlyGhostsCanTalk    = o.OnlyGhostsCanTalk;
		HearInVent           = o.HearInVent;
		VentPrivateChat      = o.VentPrivateChat;
		CommsSabDisables     = o.CommsSabDisables;
		CameraCanHear        = o.CameraCanHear;
		ImpostorPrivateRadio = o.ImpostorPrivateRadio;
		OnlyMeetingOrLobby   = o.OnlyMeetingOrLobby;
	}

	public bool ContentEquals(VoiceChatRoomSettings? o)
	{
		if (o is null) return false;
		return Math.Abs(MaxChatDistance - o.MaxChatDistance) < 0.01f
			&& WallsBlockSound      == o.WallsBlockSound
			&& OnlyHearInSight      == o.OnlyHearInSight
			&& ImpostorHearGhosts   == o.ImpostorHearGhosts
			&& OnlyGhostsCanTalk    == o.OnlyGhostsCanTalk
			&& HearInVent           == o.HearInVent
			&& VentPrivateChat      == o.VentPrivateChat
			&& CommsSabDisables     == o.CommsSabDisables
			&& CameraCanHear        == o.CameraCanHear
			&& ImpostorPrivateRadio == o.ImpostorPrivateRadio
			&& OnlyMeetingOrLobby   == o.OnlyMeetingOrLobby;
	}

	// ── RPC 序列化 ──────────────────────────────────────────────────────────
	public static void SendToAll(VoiceChatRoomSettings s)
	{
		if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;
		var w = AmongUsClient.Instance.StartRpcImmediately(
			PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
		w.Write(s.MaxChatDistance);
		w.Write(s.WallsBlockSound);
		w.Write(s.OnlyHearInSight);
		w.Write(s.ImpostorHearGhosts);
		w.Write(s.OnlyGhostsCanTalk);
		w.Write(s.HearInVent);
		w.Write(s.VentPrivateChat);
		w.Write(s.CommsSabDisables);
		w.Write(s.CameraCanHear);
		w.Write(s.ImpostorPrivateRadio);
		w.Write(s.OnlyMeetingOrLobby);
		AmongUsClient.Instance.FinishRpcImmediately(w);
	}

	[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
	public static class RpcPatch
	{
		[HarmonyPostfix]
		public static void Postfix(byte callId, MessageReader reader)
		{
			if (callId != RpcId) return;
			try
			{
				var s = VoiceChatConfig.SyncedRoomSettings;
				s.MaxChatDistance      = Math.Clamp(reader.ReadSingle(), 1.5f, 20f);
				s.WallsBlockSound      = reader.ReadBoolean();
				s.OnlyHearInSight      = reader.ReadBoolean();
				s.ImpostorHearGhosts   = reader.ReadBoolean();
				s.OnlyGhostsCanTalk    = reader.ReadBoolean();
				s.HearInVent           = reader.ReadBoolean();
				s.VentPrivateChat      = reader.ReadBoolean();
				s.CommsSabDisables     = reader.ReadBoolean();
				s.CameraCanHear        = reader.ReadBoolean();
				s.ImpostorPrivateRadio = reader.ReadBoolean();
				s.OnlyMeetingOrLobby   = reader.ReadBoolean();
				VoiceChatPluginMain.Logger.LogInfo("[VC] RoomSettings RPC received.");
			}
			catch (Exception ex)
			{
				VoiceChatPluginMain.Logger.LogError("[VC] RoomSettings RPC parse error: " + ex.Message);
			}
		}
	}
}
