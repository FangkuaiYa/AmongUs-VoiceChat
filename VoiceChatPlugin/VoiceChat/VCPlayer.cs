using System.Collections.Generic;
using VoiceChatPlugin.Audio;

using UnityEngine;
using System;

namespace VoiceChatPlugin.VoiceChat;

public class VCPlayer
{
    private readonly StereoRouter.Property     _imager;
    private readonly VolumeRouter.Property     _normalVolume, _ghostVolume, _radioVolume, _clientVolume;
    private readonly LevelMeterRouter.Property _levelMeter;

    private byte           _playerId   = byte.MaxValue;
    private string         _playerName = "Unknown";
    private PlayerControl? _mappedPlayer;

    private readonly AudioRoutingInstance _instance;

    public string PlayerName => _playerName;
    public byte   PlayerId   => _playerId;
    public float  Volume     => _clientVolume.Volume;
    public float  Level      => _levelMeter.Level;
    public bool   IsMapped   => _mappedPlayer != null && _mappedPlayer;

    public VCPlayer(
        VoiceChatRoom        room,
        AudioRoutingInstance instance,
        StereoRouter         imager,
        VolumeRouter         normalVolume,
        VolumeRouter         ghostVolume,
        VolumeRouter         radioVolume,
        VolumeRouter         clientVolume,
        LevelMeterRouter     levelMeter)
    {
        _instance     = instance;
        _imager       = imager.GetProperty(instance);
        _normalVolume = normalVolume.GetProperty(instance);
        _ghostVolume  = ghostVolume.GetProperty(instance);
        _radioVolume  = radioVolume.GetProperty(instance);
        _clientVolume = clientVolume.GetProperty(instance);
        _levelMeter   = levelMeter.GetProperty(instance);
        _clientVolume.Volume = 1f;
        MuteAll();
    }

    // Push decoded PCM into the Interstellar audio graph for this client slot.
    internal void AddSamples(float[] samples, int count)
        => _instance.AddSamples(samples, 0, count);

    public void UpdateProfile(byte playerId, string playerName)
    {
        _playerId     = playerId;
        _playerName   = playerName;
        _mappedPlayer = null;
        MuteAll();
    }

    public void ResetMapping()
    {
        _mappedPlayer = null;
        MuteAll();
    }

    // Attempt to resolve the Among Us PlayerControl from the Hazel client ID.
    internal void TryResolveFromClientId(int clientId)
    {
        if (AmongUsClient.Instance == null) return;
        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (!pc) continue;
            var cl = AmongUsClient.Instance.GetClientFromCharacter(pc);
            if (cl != null && cl.Id == clientId)
            {
                _playerId     = pc.PlayerId;
                _playerName   = pc.name;
                _mappedPlayer = pc;
                return;
            }
        }
    }

    private void CheckMapping()
    {
        if (_mappedPlayer != null && _mappedPlayer && _mappedPlayer.PlayerId == _playerId) return;
        _mappedPlayer = null;
        if (_playerId == byte.MaxValue) return;
        foreach (var p in PlayerControl.AllPlayerControls.ToArray())
            if (p.PlayerId == _playerId) { _mappedPlayer = p; break; }
    }

    public void SetVolume(float v) => _clientVolume.Volume = v;

    private void MuteAll()
    {
        _normalVolume.Volume = 0f;
        _ghostVolume.Volume  = 0f;
        _radioVolume.Volume  = 0f;
    }

    public void UpdateLobby()
    {
        CheckMapping();
        _imager.Pan          = 0f;
        _normalVolume.Volume = 1f;
        _ghostVolume.Volume  = 0f;
        _radioVolume.Volume  = 0f;
    }

    public void UpdateMeeting()
    {
        CheckMapping();
        if (!IsMapped) { MuteAll(); return; }

        var  s          = VoiceChatConfig.SyncedRoomSettings;
        bool localDead  = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.IsDead == true;
        bool targetDead = _mappedPlayer!.Data?.IsDead == true;
        bool localImp   = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true;
        bool targetImp  = _mappedPlayer.Data?.Role?.IsImpostor == true;

        _imager.Pan = 0f;

        if (s.OnlyGhostsCanTalk && !localDead) { MuteAll(); return; }

        if (localDead)
        {
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume  = targetDead ? 1f : 0f;
            _radioVolume.Volume  = 0f;
            return;
        }

        if (s.ImpostorPrivateRadio && localImp && targetImp && !targetDead)
        {
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume  = 0f;
            _radioVolume.Volume  = 1f;
            return;
        }

        if (VoiceChatPatches.IsImpostorRadioOnly && localImp)
        {
            bool hear = targetImp && !targetDead;
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume  = 0f;
            _radioVolume.Volume  = hear ? 1f : 0f;
            return;
        }

        _normalVolume.Volume = targetDead ? 0f : 1f;
        _ghostVolume.Volume  = 0f;
        _radioVolume.Volume  = 0f;
    }

    private float _wallCoeff = 1f;

    internal void UpdateTaskPhase(
        Vector2? listenerPos,
        IEnumerable<VoiceChatRoom.SpeakerCache> speakers,
        IEnumerable<IVoiceComponent> virtualMics,
        bool localInVent,
        bool commsSabActive)
    {
        CheckMapping();
        if (!IsMapped || !listenerPos.HasValue) { MuteAll(); return; }

        var  s            = VoiceChatConfig.SyncedRoomSettings;
        var  targetPos    = (Vector2)_mappedPlayer!.transform.position;
        bool localDead    = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.IsDead == true;
        bool targetDead   = _mappedPlayer.Data?.IsDead == true;
        bool localImp     = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true;
        bool targetImp    = _mappedPlayer.Data?.Role?.IsImpostor == true;
        bool targetInVent = _mappedPlayer.inVent;

        if (s.OnlyMeetingOrLobby) { MuteAll(); return; }
        if (s.OnlyGhostsCanTalk && !localDead) { MuteAll(); return; }
        if (commsSabActive && s.CommsSabDisables && !localImp && !localDead) { MuteAll(); return; }

        if (localDead)
        {
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume  = targetDead ? 1f : 0f;
            _radioVolume.Volume  = 0f;
            _imager.Pan          = 0f;
            return;
        }

        if (s.ImpostorPrivateRadio && localImp && targetImp && !targetDead)
        {
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume  = 0f;
            _radioVolume.Volume  = 1f;
            _imager.Pan          = 0f;
            return;
        }

        if (VoiceChatPatches.IsImpostorRadioOnly && localImp)
        {
            bool hear = targetImp && !targetDead;
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume  = 0f;
            _radioVolume.Volume  = hear ? 1f : 0f;
            _imager.Pan          = 0f;
            return;
        }

        if (localImp && targetDead && s.ImpostorHearGhosts)
        {
            float d   = Vector2.Distance(targetPos, listenerPos.Value);
            float vol = VoiceChatRoom.GetVolume(d, s.MaxChatDistance);
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume  = vol;
            _radioVolume.Volume  = 0f;
            _imager.Pan          = VoiceChatRoom.GetPan(listenerPos.Value.x, targetPos.x);
            return;
        }

        if (targetDead) { MuteAll(); return; }

        if (targetInVent)
        {
            if (!s.HearInVent) { MuteAll(); return; }
            if (s.VentPrivateChat && !localInVent) { MuteAll(); return; }
        }
        else if (s.VentPrivateChat && localInVent)
        {
            MuteAll(); return;
        }

        float dist   = Vector2.Distance(targetPos, listenerPos.Value);
        float volume = VoiceChatRoom.GetVolume(dist, s.MaxChatDistance);
        float pan    = VoiceChatRoom.GetPan(listenerPos.Value.x, targetPos.x);
        _imager.Pan  = pan;

        if (s.OnlyHearInSight)
        {
            bool inSight = !Physics2D.Linecast(listenerPos.Value, targetPos, LayerMask.GetMask("Shadow"));
            if (!inSight) { MuteAll(); return; }
        }

        if (s.WallsBlockSound)
        {
            bool hasWall = Physics2D.Linecast(listenerPos.Value, targetPos, LayerMask.GetMask("Shadow"));
            _wallCoeff   = _wallCoeff + ((hasWall ? 0f : 1f) - _wallCoeff) * Math.Clamp(Time.deltaTime * 4f, 0f, 1f);
        }
        else
        {
            _wallCoeff = 1f;
        }

        _normalVolume.Volume = volume * _wallCoeff;
        _ghostVolume.Volume  = 0f;
        _radioVolume.Volume  = 0f;
    }
}
