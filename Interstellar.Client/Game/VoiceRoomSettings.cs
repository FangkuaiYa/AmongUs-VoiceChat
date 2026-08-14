using System;

namespace Interstellar.Voice;

public sealed class VoiceRoomSettings
{
    public float MaxChatDistance { get; internal set; }
    public bool WallsBlockSound { get; internal set; }
    public bool OnlyHearInSight { get; internal set; }
    public bool ImpostorHearGhosts { get; internal set; }
    public bool OnlyGhostsCanTalk { get; internal set; }
    public bool HearInVent { get; internal set; }
    public bool HearVentPlayers { get; internal set; }
    public bool VentPrivateChat { get; internal set; }
    public bool CommsSabDisables { get; internal set; }
    public bool CameraCanHear { get; internal set; }
    public bool ImpostorPrivateRadio { get; internal set; }
    public bool OnlyMeetingOrLobby { get; internal set; }

    public bool CanTalkThroughWalls => !WallsBlockSound;

    public VoiceRoomSettings() { Reset(); }

    public void Reset()
    {
        MaxChatDistance = 6f;
        WallsBlockSound = true;
        OnlyHearInSight = false;
        ImpostorHearGhosts = false;
        OnlyGhostsCanTalk = false;
        HearInVent = true;
        HearVentPlayers = true;
        VentPrivateChat = false;
        CommsSabDisables = true;
        CameraCanHear = true;
        ImpostorPrivateRadio = false;
        OnlyMeetingOrLobby = false;
    }

    public void Apply(VoiceRoomSettings o)
    {
        MaxChatDistance = Math.Clamp(o.MaxChatDistance, 1.5f, 20f);
        WallsBlockSound = o.WallsBlockSound;
        OnlyHearInSight = o.OnlyHearInSight;
        ImpostorHearGhosts = o.ImpostorHearGhosts;
        OnlyGhostsCanTalk = o.OnlyGhostsCanTalk;
        HearInVent = o.HearInVent;
        HearVentPlayers = o.HearVentPlayers;
        VentPrivateChat = o.VentPrivateChat;
        CommsSabDisables = o.CommsSabDisables;
        CameraCanHear = o.CameraCanHear;
        ImpostorPrivateRadio = o.ImpostorPrivateRadio;
        OnlyMeetingOrLobby = o.OnlyMeetingOrLobby;
    }

    public bool ContentEquals(VoiceRoomSettings? o)
    {
        if (o is null) return false;
        return Math.Abs(MaxChatDistance - o.MaxChatDistance) < 0.01f
            && WallsBlockSound == o.WallsBlockSound
            && OnlyHearInSight == o.OnlyHearInSight
            && ImpostorHearGhosts == o.ImpostorHearGhosts
            && OnlyGhostsCanTalk == o.OnlyGhostsCanTalk
            && HearInVent == o.HearInVent
            && HearVentPlayers == o.HearVentPlayers
            && VentPrivateChat == o.VentPrivateChat
            && CommsSabDisables == o.CommsSabDisables
            && CameraCanHear == o.CameraCanHear
            && ImpostorPrivateRadio == o.ImpostorPrivateRadio
            && OnlyMeetingOrLobby == o.OnlyMeetingOrLobby;
    }
}
