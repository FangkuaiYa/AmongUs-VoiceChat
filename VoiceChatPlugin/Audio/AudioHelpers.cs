using Concentus;

namespace VoiceChatPlugin.Audio;

internal static class AudioHelpers
{
    public const int ClockRate = 48000;

    public static IOpusEncoder GetOpusEncoder()
        => OpusCodecFactory.CreateEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);

    public static IOpusDecoder GetOpusDecoder()
        => OpusCodecFactory.CreateDecoder(48000, 1);
}
