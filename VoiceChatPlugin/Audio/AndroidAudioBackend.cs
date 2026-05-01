#if ANDROID
// NOTE: Android audio backends.
// AndroidMicrophoneInput has been superseded by VoiceChat/AndroidMicrophone.cs
// which mirrors Nebula's ManualMicrophone + PushAudioData pattern.
// AndroidAudioOutput is kept here but not actively used;
// VoiceChat/AndroidSpeaker.cs mirrors Nebula's ManualSpeaker pattern instead.
using System;
using System.Collections.Concurrent;
using UnityEngine;
using Concentus;

namespace VoiceChatPlugin.Audio;

/// <summary>
/// Android audio output backend (retained for reference).
/// The active Android speaker backend is VoiceChatPlugin.VoiceChat.AndroidSpeaker,
/// which mirrors Nebula's ManualSpeaker + AudioSource approach exactly.
/// </summary>
public sealed class AndroidAudioOutput : IDisposable
{
    private readonly AudioSource   _source;
    private readonly AudioClip     _clip;
    private readonly ConcurrentQueue<float[]> _pendingChunks = new();

    private int   _writePos;
    private float _volume = 1f;

    private const int BufferMs      = 200;
    private const int TargetSR      = 48000;
    private const int TargetChans   = 2;

    public AndroidAudioOutput(GameObject hostObject)
    {
        int bufferSamples = TargetSR * TargetChans * BufferMs / 1000;

        _clip = AudioClip.Create(
            "VC_Output",
            TargetSR * BufferMs / 1000,
            TargetChans,
            TargetSR,
            true,
            (AudioClip.PCMReaderCallback)((ary) => FillBuffer(ary)));

        _source = hostObject.AddComponent<AudioSource>();
        _source.clip   = _clip;
        _source.loop   = true;
        _source.volume = 1f;
        _source.Play();
    }

    private void FillBuffer(float[] data)
    {
        int filled = 0;
        while (filled < data.Length)
        {
            if (!_pendingChunks.TryPeek(out var chunk)) break;
            int needed = data.Length - filled;
            int offset = _writePos % chunk.Length;
            int avail  = chunk.Length - offset;
            if (avail <= needed)
            {
                _pendingChunks.TryDequeue(out _);
                Array.Copy(chunk, 0, data, filled, avail);
                filled   += avail;
                _writePos = 0;
            }
            else
            {
                Array.Copy(chunk, offset, data, filled, needed);
                _writePos += needed;
                filled     = data.Length;
            }
        }
        if (filled < data.Length)
            Array.Clear(data, filled, data.Length - filled);
        if (_volume != 1f)
            for (int i = 0; i < data.Length; i++) data[i] *= _volume;
    }

    public void EnqueueSamples(float[] samples) => _pendingChunks.Enqueue(samples);

    public void SetVolume(float v)
    {
        _volume        = Math.Clamp(v, 0f, 4f);
        _source.volume = Math.Clamp(v, 0f, 1f);
    }

    public void Dispose()
    {
        _source.Stop();
        if (_source != null) UnityEngine.Object.Destroy(_source);
    }
}
#endif
