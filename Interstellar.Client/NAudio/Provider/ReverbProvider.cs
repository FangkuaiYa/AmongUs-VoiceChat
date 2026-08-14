using NAudio.Wave;

namespace Interstellar.Audio.Provider;

/// <summary>
/// SampleProvider that adds a reverb effect.
/// </summary>
internal class ReverbSampleProvider : ISampleProvider
{
    private readonly ISampleProvider sourceProvider;
    private readonly float[] delayBuffer;
    private int delayPosition;
    private float decay;
    private float wetMix; // Reverb wet mix level
    private float dryMix; // Dry (original) mix level

    public float Decay { get => decay; set => decay = Math.Clamp(value, 0.0f, 1.0f); }
    public float WetDryMix { get => wetMix; set { wetMix = Math.Clamp(value, 0.0f, 1.0f); dryMix = 1.0f - wetMix; } }

    public WaveFormat WaveFormat => sourceProvider.WaveFormat;

    /// <summary>
    /// Adds a reverb/delay effect to the audio signal.
    /// </summary>
    /// <param name="sourceProvider">Input audio source.</param>
    /// <param name="delayMilliseconds">Delay in milliseconds.</param>
    /// <param name="decay">Decay factor (0.0 - 1.0).</param>
    /// <param name="wetDryMix">Mix balance (0.0 = dry only, 1.0 = wet only).</param>
    public ReverbSampleProvider(ISampleProvider sourceProvider, int delayMilliseconds, float decay, float wetDryMix)
    {
        this.sourceProvider = sourceProvider;
        int delaySamples = (int)(WaveFormat.SampleRate * (delayMilliseconds / 1000.0f)) * WaveFormat.Channels;
        delayBuffer = new float[delaySamples];
        this.decay = decay;
        this.wetMix = wetDryMix;
        this.dryMix = 1.0f - wetDryMix;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = sourceProvider.Read(buffer, offset, count);
        
        for (int i = 0; i < samplesRead; i++)
        {
            float currentSample = buffer[offset + i];            
            float delayedSample = delayBuffer[delayPosition];

            delayBuffer[delayPosition] = currentSample + (delayedSample * decay);
            buffer[offset + i] = (currentSample * dryMix) + (delayedSample * wetMix);

            delayPosition = (delayPosition + 1) % delayBuffer.Length;
        }
        return samplesRead;
    }
}