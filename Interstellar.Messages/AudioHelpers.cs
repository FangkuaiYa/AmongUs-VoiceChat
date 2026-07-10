using Concentus;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interstellar.Messages;

static public class AudioHelpers
{
    public const int ClockRate = 48000;
    static public AudioFormat GetOpusFormat(int id) => new AudioFormat(AudioCodecsEnum.OPUS, id, ClockRate);
    static public IOpusEncoder GetOpusEncoder()
    {
        var encoder = OpusCodecFactory.CreateEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
        // 64 kbps — high-quality voice. Opus reaches transparency for speech around 32-48 kbps;
        // 64 kbps provides headroom for music, overlapping speakers, and noisy environments.
        // Bandwidth is cheap; clarity matters more.
        encoder.Bitrate = 64000;
        encoder.UseVBR = true;
        // DTX disabled — prevents speech cut-in at the start of utterances
        encoder.UseDTX = false;
        // In-band FEC: mitigates packet loss without extra bandwidth
        encoder.UseInbandFEC = true;
        encoder.SignalType = Concentus.Enums.OpusSignal.OPUS_SIGNAL_VOICE;
        return encoder;
    }
    static public IOpusDecoder GetOpusDecoder() => OpusCodecFactory.CreateDecoder(48000, 1);

    public const int MaxTracks = 63;
}
