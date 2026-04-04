using VoiceChatPlugin.Concentus;
using VoiceChatPlugin.Concentus.Enums;
using VoiceChatPlugin.Concentus.Structs;

namespace VoiceChatPlugin.Audio;

internal static class AudioHelpers
{
    public const int ClockRate = 48000;

    // Directly instantiate the managed Opus encoder/decoder.
    // This bypasses OpusCodecFactory's native-library probe which would
    // throw on game startup (no libopus.dll present in the game directory).
    public static IOpusEncoder GetOpusEncoder()
        => new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);

    public static IOpusDecoder GetOpusDecoder()
        => new OpusDecoder(48000, 1);
}
