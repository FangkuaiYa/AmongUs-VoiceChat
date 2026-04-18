#if ANDROID
using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android audio output bridge.
///
/// On Android, NAudio's <c>WasapiOut</c> is unavailable. Instead we create a
/// looping <see cref="AudioClip"/> in streaming mode and attach it to a Unity
/// <see cref="AudioSource"/>.  Unity pulls PCM data via the
/// <c>PCMReaderCallback</c>, which we fill from the same decoded float samples
/// that the Windows <c>WasapiOut</c> pulls from the audio graph.
///
/// This mirrors the approach used in Nebula (NoSVCRoom.cs):
/// <code>
///   var audioSource = ... AddComponent&lt;AudioSource&gt;();
///   AudioClip myClip = AudioClip.Create("VCAudio", halfSecSamples, 2, sampleRate, true,
///       (PCMReaderCallback)(ary =&gt; speaker.Read(ary)));
///   audioSource.clip = myClip;
///   audioSource.loop = true;
///   audioSource.Play();
/// </code>
///
/// The <see cref="DecodedBuffer"/> queue is written by the main-thread decode
/// loop in <see cref="VoiceChatRoom"/> and read by Unity's audio thread via the
/// PCMReaderCallback.
/// </summary>
internal sealed class AndroidSpeaker : IDisposable
{
    private const int SampleRate    = 48000;
    private const int Channels      = 2;      // stereo output
    private const float HalfSecBuf  = 0.5f;   // streaming clip half-second length

    private readonly GameObject    _hostObject;
    private readonly AudioSource   _audioSource;
    private readonly AudioClip     _streamingClip;

    // Ring buffer for decoded stereo PCM; written on main thread, read on audio thread.
    // Use a simple lock-free approach: write index wraps around a float[].
    private readonly float[] _ring;
    private volatile int     _writePos;
    private volatile int     _readPos;
    private readonly int     _ringSize;

    public bool IsPlaying => _audioSource != null && _audioSource.isPlaying;

    // Master volume [0, 2]. Called by VoiceChatRoom.SetMasterVolume().
    private float _masterVolume = 1f;

    public void SetMasterVolume(float v)
    {
        _masterVolume = Math.Clamp(v, 0f, 2f);
        if (_audioSource != null)
            _audioSource.volume = Math.Clamp(v, 0f, 1f);
        // Drain the ring on mute to prevent residual audio on unmute
        if (_masterVolume <= 0f) _readPos = _writePos;
    }

    /// <summary>
    /// Create and immediately start the Android speaker output.
    /// <paramref name="hostTransform"/> should be a persistent scene object
    /// (e.g., the HudManager transform or a DontDestroyOnLoad object).
    /// </summary>
    public AndroidSpeaker(Transform hostTransform)
    {
        int clipSamples = (int)(SampleRate * HalfSecBuf);
        _ringSize       = clipSamples * Channels * 4; // keep 4× buffer depth
        _ring           = new float[_ringSize];
        _writePos       = 0;
        _readPos        = 0;

        _hostObject  = new GameObject("VC_AndroidSpeaker");
        _hostObject.transform.SetParent(hostTransform, false);
        UnityEngine.Object.DontDestroyOnLoad(_hostObject);

        _audioSource = _hostObject.AddComponent<AudioSource>();
        _audioSource.spatialBlend = 0f; // 2D output

        // Streaming clip: Unity calls the PCM reader on its audio thread.
        // IL2CPP Among Us uses Action<Il2CppStructArray<float>>, not the managed
        // PCMReaderCallback delegate, so we cast accordingly.
        _streamingClip = AudioClip.Create(
            "VCAndroidOutput",
            clipSamples * Channels,
            Channels,
            SampleRate,
            stream: true,
            new Action<Il2CppStructArray<float>>(ReadCallbackIl2Cpp));

        _audioSource.clip = _streamingClip;
        _audioSource.loop = true;
        _audioSource.Play();
    }

    // ── Called by audio thread (Unity internals) ─────────────────────────────
    // IL2CPP passes an Il2CppStructArray<float>; we treat it as a regular array.

    private void ReadCallbackIl2Cpp(Il2CppStructArray<float> data)
    {
        int needed = data.Length;
        int avail  = AvailableForRead();

        if (avail >= needed)
        {
            for (int i = 0; i < needed; i++)
            {
                data[i]  = _ring[_readPos % _ringSize];
                _readPos = (_readPos + 1) % _ringSize;
            }
        }
        else
        {
            // Under-run: output silence for missing samples
            int copyCount = avail;
            for (int i = 0; i < copyCount; i++)
            {
                data[i]  = _ring[_readPos % _ringSize];
                _readPos = (_readPos + 1) % _ringSize;
            }
            for (int i = copyCount; i < needed; i++) data[i] = 0f;
        }
    }

    // ── Called by main thread decode loop ────────────────────────────────────

    /// <summary>
    /// Push decoded stereo (or mono-upscaled) float PCM into the output ring.
    /// <paramref name="samples"/> must be interleaved stereo at 48 kHz.
    /// </summary>
    public void Write(float[] samples, int offset, int count)
    {
        // If the ring is full, drop oldest data (overwrite read pointer)
        for (int i = 0; i < count; i++)
        {
            _ring[_writePos] = samples[offset + i];
            _writePos        = (_writePos + 1) % _ringSize;

            // If write overtook read, advance read to discard oldest sample
            if (_writePos == _readPos)
                _readPos = (_readPos + 1) % _ringSize;
        }
    }

    /// <summary>Convenience overload for mono samples (duplicates to stereo).</summary>
    public void WriteMono(float[] samples, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float s = samples[offset + i];
            _ring[_writePos] = s;
            _writePos        = (_writePos + 1) % _ringSize;
            if (_writePos == _readPos) _readPos = (_readPos + 1) % _ringSize;

            _ring[_writePos] = s; // duplicate for right channel
            _writePos        = (_writePos + 1) % _ringSize;
            if (_writePos == _readPos) _readPos = (_readPos + 1) % _ringSize;
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_audioSource != null) _audioSource.Stop();
        if (_hostObject  != null) UnityEngine.Object.Destroy(_hostObject);
        VoiceChatPluginMain.Logger.LogInfo("[VC] Android speaker disposed.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private int AvailableForRead()
    {
        int w = _writePos, r = _readPos;
        return w >= r ? w - r : _ringSize - r + w;
    }
}
#endif
