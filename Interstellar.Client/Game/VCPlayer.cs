using Interstellar.Routing;
using Interstellar.Routing.Router;
using UnityEngine;

namespace Interstellar.Voice;

public class VCPlayer
{
    private readonly VoiceRoom _room;
    private readonly int _clientId;
    private readonly StereoRouter.Property _imager;
    private readonly VolumeRouter.Property _normalVolume, _ghostVolume, _radioVolume, _clientVolume;
    private readonly LevelMeterRouter.Property _levelMeter;

    private byte _playerId = byte.MaxValue;
    private string _playerName = "Unknown";
    private PlayerControl? _mappedPlayer;

    public string PlayerName => _playerName;
    public byte PlayerId => _playerId;
    public int ClientId => _clientId;
    public float Volume => _clientVolume.Volume;
    public float Level => _levelMeter.Level;
    public bool IsMapped => _mappedPlayer != null && _mappedPlayer;
    public bool IsAudible => _normalVolume.Volume > 0f || _ghostVolume.Volume > 0f || _radioVolume.Volume > 0f;

    // ----- Shadow / wall detection -----
    // Uses the exact same method + mask as Among Us's own vision checks
    // (see NoShadowBehaviour, Console, etc.).

    /// <summary>
    /// Returns true when a real wall/shadow collider stands between
    /// <paramref name="source"/> and <paramref name="target"/>.
    /// </summary>
    private static bool HasShadowBetween(Vector2 source, Vector2 target)
    {
        return PhysicsHelpers.AnythingBetween(source, target, Constants.ShadowMask, false);
    }

    public VCPlayer(
        VoiceRoom room,
        AudioRoutingInstance instance,
        StereoRouter imager,
        VolumeRouter normalVolume,
        VolumeRouter ghostVolume,
        VolumeRouter radioVolume,
        VolumeRouter clientVolume,
        LevelMeterRouter levelMeter)
    {
        _room = room;
        _clientId = instance.ClientId;
        _imager = imager.GetProperty(instance);
        _normalVolume = normalVolume.GetProperty(instance);
        _ghostVolume = ghostVolume.GetProperty(instance);
        _radioVolume = radioVolume.GetProperty(instance);
        _clientVolume = clientVolume.GetProperty(instance);
        _levelMeter = levelMeter.GetProperty(instance);
        _clientVolume.Volume = 1f;
        MuteAll();
    }

    public void UpdateProfile(byte playerId, string playerName)
    {
        _playerId = playerId;
        _playerName = playerName;
        _mappedPlayer = null;
        MuteAll();
    }

    public void ResetMapping()
    {
        _mappedPlayer = null;
        MuteAll();
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
        _ghostVolume.Volume = 0f;
        _radioVolume.Volume = 0f;
    }

    public void UpdateLobby()
    {
        CheckMapping();
        _imager.Pan = 0f;
        _normalVolume.Volume = 1f;
        _ghostVolume.Volume = 0f;
        _radioVolume.Volume = 0f;
    }

    public void UpdateMeeting()
    {
        CheckMapping();
        if (!IsMapped) { MuteAll(); return; }

        var s = VoiceConfig.SyncedRoomSettings;
        bool localDead = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.IsDead == true;
        bool targetDead = _mappedPlayer!.Data?.IsDead == true;
        bool localImp = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true;
        bool targetImp = _mappedPlayer.Data?.Role?.IsImpostor == true;

        _imager.Pan = 0f;

        if (s.OnlyGhostsCanTalk && !localDead) { MuteAll(); return; }

        // Dead players hear everyone during meetings
        if (localDead)
        {
            _normalVolume.Volume = targetDead ? 0f : 1f;
            _ghostVolume.Volume = targetDead ? 1f : 0f;
            _radioVolume.Volume = 0f;
            return;
        }

        // Non-impostors don't hear impostors who are in impostor radio mode
        bool spkInRadio = _room.IsClientImpostorRadio(_clientId);
        if (s.ImpostorPrivateRadio && targetImp && !targetDead && !localImp && spkInRadio)
        {
            MuteAll();
            return;
        }

        if (s.ImpostorPrivateRadio && localImp && targetImp && !targetDead)
        {
            _normalVolume.Volume = spkInRadio ? 0f : 1f;
            _ghostVolume.Volume = 0f;
            _radioVolume.Volume = spkInRadio ? 1f : 0f;
            return;
        }

        // Local mic in impostor-only mode: hear impostors on radio, normal on normal
        if (InterstellarHudState.IsImpostorRadioOnly && localImp)
        {
            if (targetImp && !targetDead)
            {
                _normalVolume.Volume = 0f;
                _ghostVolume.Volume = 0f;
                _radioVolume.Volume = 1f;
            }
            else if (!targetDead)
            {
                _normalVolume.Volume = 1f;
                _ghostVolume.Volume = 0f;
                _radioVolume.Volume = 0f;
            }
            else
            {
                MuteAll();
            }
            return;
        }

        _normalVolume.Volume = targetDead ? 0f : 1f;
        _ghostVolume.Volume = 0f;
        _radioVolume.Volume = 0f;
    }

    private float _wallCoeff = 1f;

    private static float CalcWallCoeff(Vector2 listener, Vector2 speaker, ref float coeff, VoiceRoomSettings s)
    {
        if (!s.WallsBlockSound) { coeff = 1f; return 1f; }

        bool hasWall = HasShadowBetween(listener, speaker);
        coeff = coeff + ((hasWall ? 0f : 1f) - coeff) * Math.Clamp(Time.deltaTime * 4f, 0f, 1f);
        return coeff;
    }

    internal void UpdateTaskPhase(
        Vector2? listenerPos,
        IEnumerable<VoiceRoom.SpeakerCache> speakers,
        IEnumerable<IVoiceComponent> virtualMics,
        bool localInVent,
        bool commsSabActive)
    {
        CheckMapping();
        if (!IsMapped || !listenerPos.HasValue) { MuteAll(); return; }

        var s = VoiceConfig.SyncedRoomSettings;
        var targetPos = (Vector2)_mappedPlayer!.transform.position;
        bool localDead = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.IsDead == true;
        bool targetDead = _mappedPlayer.Data?.IsDead == true;
        bool localImp = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true;
        bool targetImp = _mappedPlayer.Data?.Role?.IsImpostor == true;
        bool targetInVent = _mappedPlayer.inVent;

        if (s.OnlyMeetingOrLobby) { MuteAll(); return; }
        if (s.OnlyGhostsCanTalk && !localDead) { MuteAll(); return; }
        if (commsSabActive && s.CommsSabDisables && !localImp && !localDead) { MuteAll(); return; }

        float dist = Vector2.Distance(targetPos, listenerPos.Value);
        float volume = VoiceRoom.GetVolume(dist, s.MaxChatDistance);
        float pan = VoiceRoom.GetPan(listenerPos.Value.x, targetPos.x);

        if (localDead)
        {
            // Dead players hear: alive on normal (spatial), dead on ghost
            if (targetDead)
            {
                _normalVolume.Volume = 0f;
                _ghostVolume.Volume = 1f;
                _radioVolume.Volume = 0f;
                _imager.Pan = 0f;
            }
            else
            {
                _normalVolume.Volume = volume * CalcWallCoeff(listenerPos.Value, targetPos, ref _wallCoeff, s);
                _ghostVolume.Volume = 0f;
                _radioVolume.Volume = 0f;
                _imager.Pan = pan;
            }
            return;
        }

        // Non-impostors don't hear impostors who are in impostor radio mode
        bool spkInRadio = _room.IsClientImpostorRadio(_clientId);
        if (s.ImpostorPrivateRadio && targetImp && !targetDead && !localImp && spkInRadio)
        {
            MuteAll();
            return;
        }

        // Impostors hear each other on radio channel when speaker is in radio mode
        if (s.ImpostorPrivateRadio && localImp && targetImp && !targetDead && spkInRadio)
        {
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume = 0f;
            _radioVolume.Volume = 1f;
            _imager.Pan = 0f;
            return;
        }

        // Local mic in impostor-only mode: hear impostors on radio, normal players on normal
        if (InterstellarHudState.IsImpostorRadioOnly && localImp)
        {
            if (targetImp && !targetDead)
            {
                _normalVolume.Volume = 0f;
                _ghostVolume.Volume = 0f;
                _radioVolume.Volume = 1f;
            }
            else if (!targetDead)
            {
                _normalVolume.Volume = volume * CalcWallCoeff(listenerPos.Value, targetPos, ref _wallCoeff, s);
                _ghostVolume.Volume = 0f;
                _radioVolume.Volume = 0f;
            }
            else
            {
                MuteAll();
            }
            _imager.Pan = 0f;
            return;
        }

        if (localImp && targetDead && s.ImpostorHearGhosts)
        {
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume = volume;
            _radioVolume.Volume = 0f;
            _imager.Pan = pan;
            return;
        }

        if (targetDead) { MuteAll(); return; }

        if (targetInVent)
        {
            // HearVentPlayers: can outside players hear someone in a vent?
            if (!s.HearVentPlayers) { MuteAll(); return; }
            if (s.VentPrivateChat && !localInVent) { MuteAll(); return; }
        }
        else if (localInVent)
        {
            // HearInVent: can a player hiding in a vent hear outside players?
            if (!s.HearInVent) { MuteAll(); return; }
            if (s.VentPrivateChat) { MuteAll(); return; }
        }

        _imager.Pan = pan;
        _normalVolume.Volume = volume * CalcWallCoeff(listenerPos.Value, targetPos, ref _wallCoeff, s);
        _ghostVolume.Volume = 0f;
        _radioVolume.Volume = 0f;
    }
}
