using Interstellar.Routing;
using Interstellar.Routing.Router;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public class VCPlayer
{
	private readonly StereoRouter.Property _imager;
	private readonly VolumeRouter.Property _normalVolume, _ghostVolume, _radioVolume, _clientVolume;
	private readonly LevelMeterRouter.Property _levelMeter;

	private byte _playerId = byte.MaxValue;
	private string _playerName = "Unknown";
	private PlayerControl? _mappedPlayer;

	public string PlayerName => _playerName;
	public byte PlayerId => _playerId;
	public float Volume => _clientVolume.Volume;
	public float Level => _levelMeter.Level;
	public bool IsMapped => _mappedPlayer != null && _mappedPlayer;

	public VCPlayer(
		VoiceChatRoom room,
		AudioRoutingInstance instance,
		StereoRouter imager,
		VolumeRouter normalVolume,
		VolumeRouter ghostVolume,
		VolumeRouter radioVolume,
		VolumeRouter clientVolume,
		LevelMeterRouter levelMeter)
	{
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

	public void ResetMapping() => _mappedPlayer = null;

	private void CheckMapping()
	{
		if (_mappedPlayer != null && _mappedPlayer && _mappedPlayer.PlayerId == _playerId) return;
		_mappedPlayer = null;
		if (_playerId == byte.MaxValue) return;

		foreach (var p in PlayerControl.AllPlayerControls.ToArray())
		{
			if (p.PlayerId == _playerId) { _mappedPlayer = p; break; }
		}
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

		bool localDead = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.IsDead == true;
		bool targetDead = _mappedPlayer!.Data?.IsDead == true;
		bool localImpostor = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true;
		bool targetImpostor = _mappedPlayer.Data?.Role?.IsImpostor == true;
		bool canHear = localDead || !targetDead;

		_imager.Pan = 0f;
		_normalVolume.Volume = canHear ? 1f : 0f;
		_radioVolume.Volume = (!localDead && localImpostor && targetImpostor && !targetDead) ? 1f : 0f;
		_ghostVolume.Volume = 0f;
	}

	private float _wallCoeff = 1f;

	internal void UpdateTaskPhase(
		Vector2? listenerPos,
		IEnumerable<VoiceChatRoom.SpeakerCache> speakers,
		IEnumerable<IVoiceComponent> virtualMics)
	{
		CheckMapping();
		if (!IsMapped || !listenerPos.HasValue) { MuteAll(); return; }

		var targetPos = (Vector2)_mappedPlayer!.transform.position;
		bool localDead = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.IsDead == true;
		bool targetDead = _mappedPlayer.Data?.IsDead == true;
		bool localImpostor = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true;
		bool targetImpostor = _mappedPlayer.Data?.Role?.IsImpostor == true;
		float hearRange = VoiceChatConfig.SyncedRoomSettings.MaxChatDistance;

		float distance = Vector2.Distance(targetPos, listenerPos.Value);
		float volume = VoiceChatRoom.GetVolume(distance, hearRange);
		float pan = VoiceChatRoom.GetPan(listenerPos.Value.x, targetPos.x);

		_imager.Pan = pan;

		if (VoiceChatConfig.SyncedRoomSettings.CanTalkThroughWalls)
		{
			_wallCoeff = 1f;
		}
		else
		{
			bool hasWall = Physics2D.Linecast(listenerPos.Value, targetPos, LayerMask.GetMask("Shadow"));
			_wallCoeff = Lerp(_wallCoeff, hasWall ? 0f : 1f, Time.deltaTime * 4f);
		}

		if (localDead)
		{
			_normalVolume.Volume = targetDead ? 1f : 0f;
			_radioVolume.Volume = 0f;
			_ghostVolume.Volume = targetDead ? 1f : 0f;
			_imager.Pan = 0f;
			return;
		}

		_normalVolume.Volume = targetDead ? 0f : (volume * _wallCoeff);
		_radioVolume.Volume = (localImpostor && targetImpostor && !targetDead) ? 1f : 0f;
		_ghostVolume.Volume = 0f;
	}

	private static float Lerp(float a, float b, float t) =>
		a + (b - a) * Math.Clamp(t, 0f, 1f);
}
