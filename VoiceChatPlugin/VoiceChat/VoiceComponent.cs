using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public interface IVoiceComponent
{
    float   Radious  { get; }
    float   Volume   { get; }
    Vector2 Position { get; }
    bool    CanPlaySoundFrom(IVoiceComponent mic);

    float CanCatch(object player, Vector2 position)
    {
        float dis = Vector2.Distance(position, Position);
        if (dis < Radious) return 1f - dis / Radious;
        return 0f;
    }
}
