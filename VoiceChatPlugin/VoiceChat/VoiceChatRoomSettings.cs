using Hazel;
using HarmonyLib;
using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Room-level voice rules owned by the host and synced to every client via RPC 201.
/// </summary>
public sealed class VoiceChatRoomSettings
{
    private const byte RpcId = 201;

    public float MaxChatDistance      { get; internal set; }
    public bool  WallsBlockSound      { get; internal set; }
    public bool  OnlyHearInSight      { get; internal set; }
    public bool  ImpostorHearGhosts   { get; internal set; }
    public bool  OnlyGhostsCanTalk    { get; internal set; }
    public bool  HearInVent           { get; internal set; }
    public bool  VentPrivateChat      { get; internal set; }
    public bool  CommsSabDisables     { get; internal set; }
    public bool  CameraCanHear        { get; internal set; }
    public bool  ImpostorPrivateRadio { get; internal set; }
    public bool  OnlyMeetingOrLobby   { get; internal set; }

    public bool CanTalkThroughWalls => !WallsBlockSound;

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
