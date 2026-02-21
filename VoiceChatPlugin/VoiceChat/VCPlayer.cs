using Interstellar.Routing;
using Interstellar.Routing.Router;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public class VCPlayer
{
	private readonly StereoRouter.Property      _imager;
	private readonly VolumeRouter.Property      _normalVolume, _ghostVolume, _radioVolume, _clientVolume;
	private readonly LevelMeterRouter.Property  _levelMeter;

	private byte          _playerId   = byte.MaxValue;
	private string        _playerName = "Unknown";
	private PlayerControl? _mappedPlayer;

	public string PlayerName => _playerName;
	public byte   PlayerId   => _playerId;
	public float  Volume     => _clientVolume.Volume;
	public float  Level      => _levelMeter.Level;
	public bool   IsMapped   => _mappedPlayer != null && _mappedPlayer;

	public VCPlayer(
		VoiceChatRoom    room,
		AudioRoutingInstance instance,
		StereoRouter     imager,
		VolumeRouter     normalVolume,
		VolumeRouter     ghostVolume,
		VolumeRouter     radioVolume,
		VolumeRouter     clientVolume,
		LevelMeterRouter levelMeter)
	{
		_imager       = imager.GetProperty(instance);
		_normalVolume = normalVolume.GetProperty(instance);
		_ghostVolume  = ghostVolume.GetProperty(instance);
		_radioVolume  = radioVolume.GetProperty(instance);
		_clientVolume = clientVolume.GetProperty(instance);
		_levelMeter   = levelMeter.GetProperty(instance);
		_clientVolume.Volume = 1f;
		MuteAll();
	}

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
		MuteAll(); // 清除旧音量状态，防止上一局残留
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

	// ── 大厅（任何人都能听见彼此）─────────
	public void UpdateLobby()
	{
		CheckMapping();
		_imager.Pan          = 0f;
		_normalVolume.Volume = 1f;
		_ghostVolume.Volume  = 0f;
		_radioVolume.Volume  = 0f;
	}

	// ── 会议 ──────────────────────────────
	public void UpdateMeeting()
	{
		CheckMapping();
		if (!IsMapped) { MuteAll(); return; }

		var  s            = VoiceChatConfig.SyncedRoomSettings;
		bool localDead    = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.IsDead == true;
		bool targetDead   = _mappedPlayer!.Data?.IsDead == true;
		bool localImp     = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true;
		bool targetImp    = _mappedPlayer.Data?.Role?.IsImpostor == true;

		_imager.Pan = 0f;

		// 只有幽灵语音：活人全静音
		if (s.OnlyGhostsCanTalk && !localDead) { MuteAll(); return; }

		// 死亡者只听死亡者
		if (localDead)
		{
			_normalVolume.Volume = 0f;
			_ghostVolume.Volume  = targetDead ? 1f : 0f;
			_radioVolume.Volume  = 0f;
			return;
		}

		// 内鬼私密通话：内鬼之间用 radio 效果，其他人听不见
		if (s.ImpostorPrivateRadio && localImp && targetImp && !targetDead)
		{
			_normalVolume.Volume = 0f;
			_ghostVolume.Volume  = 0f;
			_radioVolume.Volume  = 1f;
			return;
		}

		// 本地是内鬼且开启了内鬼频道模式
		if (VoiceChatPatches.IsImpostorRadioOnly && localImp)
		{
			bool hear = targetImp && !targetDead;
			_normalVolume.Volume = 0f;
			_ghostVolume.Volume  = 0f;
			_radioVolume.Volume  = hear ? 1f : 0f;
			return;
		}

		// 普通活人听活人
		_normalVolume.Volume = targetDead ? 0f : 1f;
		_ghostVolume.Volume  = 0f;
		_radioVolume.Volume  = 0f;
	}

	// ── 任务阶段 ───────────────────────────
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

		var  s          = VoiceChatConfig.SyncedRoomSettings;
		var  targetPos  = (Vector2)_mappedPlayer!.transform.position;
		bool localDead  = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.IsDead == true;
		bool targetDead = _mappedPlayer.Data?.IsDead == true;
		bool localImp   = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true;
		bool targetImp  = _mappedPlayer.Data?.Role?.IsImpostor == true;
		bool targetInVent = _mappedPlayer.inVent;

		// 仅会议/大厅语音 → 任务阶段全静音
		if (s.OnlyMeetingOrLobby) { MuteAll(); return; }

		// 只有幽灵语音
		if (s.OnlyGhostsCanTalk && !localDead) { MuteAll(); return; }

		// 破坏通讯禁音（非内鬼活人）
		if (commsSabActive && s.CommsSabDisables && !localImp && !localDead)
		{ MuteAll(); return; }

		// 死亡者
		if (localDead)
		{
			// 死亡者只听死亡者，无空间效果
			_normalVolume.Volume = 0f;
			_ghostVolume.Volume  = targetDead ? 1f : 0f;
			_radioVolume.Volume  = 0f;
			_imager.Pan          = 0f;
			return;
		}

		// 内鬼私密通话
		if (s.ImpostorPrivateRadio && localImp && targetImp && !targetDead)
		{
			_normalVolume.Volume = 0f;
			_ghostVolume.Volume  = 0f;
			_radioVolume.Volume  = 1f;
			_imager.Pan          = 0f;
			return;
		}

		// 内鬼频道模式（玩家手动切换）
		if (VoiceChatPatches.IsImpostorRadioOnly && localImp)
		{
			bool hear = targetImp && !targetDead;
			_normalVolume.Volume = 0f;
			_ghostVolume.Volume  = 0f;
			_radioVolume.Volume  = hear ? 1f : 0f;
			_imager.Pan          = 0f;
			return;
		}

		// 内鬼听见幽灵
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

		// 目标已死且本地不是内鬼 → 听不见
		if (targetDead) { MuteAll(); return; }

		// 管道逻辑
		if (targetInVent)
		{
			if (!s.HearInVent) { MuteAll(); return; }
			// 管道私聊：在管道内的人只能跟同管道的人说话
			if (s.VentPrivateChat && !localInVent) { MuteAll(); return; }
		}
		else if (s.VentPrivateChat && localInVent)
		{
			// 本地在管道里但对方不在 → 静音
			MuteAll(); return;
		}

		// 计算距离 & 音量
		float dist    = Vector2.Distance(targetPos, listenerPos.Value);
		float volume  = VoiceChatRoom.GetVolume(dist, s.MaxChatDistance);
		float pan     = VoiceChatRoom.GetPan(listenerPos.Value.x, targetPos.x);
		_imager.Pan   = pan;

		// 仅视野内
		if (s.OnlyHearInSight)
		{
			bool inSight = !Physics2D.Linecast(listenerPos.Value, targetPos,
				LayerMask.GetMask("Shadow"));
			if (!inSight) { MuteAll(); return; }
		}

		// 墙壁隔音（平滑系数）
		if (s.WallsBlockSound)
		{
			bool hasWall  = Physics2D.Linecast(listenerPos.Value, targetPos, LayerMask.GetMask("Shadow"));
			_wallCoeff    = _wallCoeff + ((hasWall ? 0f : 1f) - _wallCoeff) * Math.Clamp(Time.deltaTime * 4f, 0f, 1f);
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
