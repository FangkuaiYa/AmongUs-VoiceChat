using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public interface IVoiceComponent
{
	public float Radious { get; }
	public float Volume { get; }
	public Vector2 Position { get; }
	public bool CanPlaySoundFrom(IVoiceComponent mic);

	public float CanCatch(object player, Vector2 position)
	{
		float dis = Vector2.Distance(position, Position);
		if (dis < Radious) return 1f - dis / Radious;
		return 0f;
	}
}